using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SketchProMax.Chat;

/// <summary>
/// A program Sketch ProMax starts from inside Revit (Claude Code for the chat, its jobs, the brief and PDF helpers, the
/// sign-in), started so that it inherits ONLY its own input/output pipes, never Revit's open files.
/// <para>
/// Process.Start with redirected input/output starts the program with every inheritable handle Revit has open. Revit
/// opens the central model's lock files (_backup\users.dat, editingperms.dat, editingrequests.dat, worksetperms.dat)
/// with inheritable handles, so a chat started while they were open kept them open for its whole life, and Synchronize
/// with Central waited on "LockUsers RW busy" until the chat ended: Revit froze for 27 minutes (bug 72). Here the
/// program is started with an explicit list of the handles it may inherit (PROC_THREAD_ATTRIBUTE_HANDLE_LIST).
/// </para>
/// Off Windows (the unit tests) it is plain Process.Start.
/// </summary>
internal sealed class ChildProcess : IDisposable
{
    /// <summary>The started program; HasExited, WaitForExit, Exited, Id and ExitCode work as usual.</summary>
    public Process Process { get; }

    /// <summary>Its input (UTF-8 without a byte order mark, flushed on every write), or null when not redirected.</summary>
    public StreamWriter? StandardInput { get; }

    /// <summary>Its output, or null when not redirected.</summary>
    public StreamReader? StandardOutput { get; }

    /// <summary>Its error output, or null when not redirected.</summary>
    public StreamReader? StandardError { get; }

    /// <summary>True when the last start used the handle list (Windows); false when it fell back to Process.Start.</summary>
    internal static bool LastStartIsolated { get; private set; }

    private ChildProcess(Process process, StreamWriter? input, StreamReader? output, StreamReader? error)
    {
        Process = process;
        StandardInput = input;
        StandardOutput = output;
        StandardError = error;
    }

    public bool HasExited
    {
        get
        {
            try { return Process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public int Id => Process.Id;

    public bool WaitForExit(int ms) => Process.WaitForExit(ms);

    public void Dispose()
    {
        try { StandardInput?.Dispose(); } catch (Exception) { }
        try { StandardOutput?.Dispose(); } catch (Exception) { }
        try { StandardError?.Dispose(); } catch (Exception) { }
        Process.Dispose();
    }

    /// <summary>
    /// Starts <paramref name="info"/> (UseShellExecute must be false). The program inherits nothing but the pipes of the
    /// streams <paramref name="info"/> redirects (the others go to NUL). Throws Win32Exception like Process.Start.
    /// </summary>
    public static ChildProcess Start(ProcessStartInfo info)
    {
        if (info.UseShellExecute) throw new InvalidOperationException("ChildProcess starts programs directly, not through the shell.");
        if (!IsWindows)
        {
            LastStartIsolated = false;
            var plain = Process.Start(info) ?? throw new InvalidOperationException(info.FileName + " didn't start.");
            return new ChildProcess(plain,
                info.RedirectStandardInput ? new StreamWriter(plain.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true } : null,
                info.RedirectStandardOutput ? plain.StandardOutput : null,
                info.RedirectStandardError ? plain.StandardError : null);
        }
        var started = StartIsolated(info);
        LastStartIsolated = true;
        return started;
    }

    private static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

    /// <summary>The command line Process.Start would build: the program in quotes, then the arguments.</summary>
    internal static string CommandLine(string fileName, string arguments)
    {
        var file = fileName.Trim();
        var quoted = file.StartsWith("\"", StringComparison.Ordinal) ? file : "\"" + file + "\"";
        return string.IsNullOrEmpty(arguments) ? quoted : quoted + " " + arguments;
    }

    /// <summary>The environment block CreateProcess takes: NAME=value pairs sorted by name, each ending in \0, then \0.</summary>
    internal static string EnvironmentBlock(IEnumerable<KeyValuePair<string, string?>> variables)
    {
        var sb = new StringBuilder();
        foreach (var pair in variables.Where(p => !string.IsNullOrEmpty(p.Key)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(pair.Key).Append('=').Append(pair.Value ?? "").Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    private static ChildProcess StartIsolated(ProcessStartInfo info)
    {
        var toClose = new List<IntPtr>();
        IntPtr attributes = IntPtr.Zero;
        GCHandle pinned = default;
        SafeFileHandle? parentIn = null, parentOut = null, parentErr = null;
        try
        {
            IntPtr nul = IntPtr.Zero;
            IntPtr Nul()
            {
                if (nul != IntPtr.Zero) return nul;
                var sa = Inheritable();
                nul = CreateFile("NUL", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, ref sa, OPEN_EXISTING, 0, IntPtr.Zero);
                if (nul == INVALID_HANDLE_VALUE) throw new Win32Exception();
                toClose.Add(nul);
                return nul;
            }

            // Child end inheritable, our end not (else a program started later would hold the pipe open, and we'd never see its end).
            IntPtr Pipe(bool childReads, out SafeFileHandle parentEnd)
            {
                var sa = Inheritable();
                if (!CreatePipe(out var read, out var write, ref sa, 0)) throw new Win32Exception();
                var (child, parent) = childReads ? (read, write) : (write, read);
                toClose.Add(child);
                SetHandleInformation(parent, HANDLE_FLAG_INHERIT, 0);
                parentEnd = new SafeFileHandle(parent, true);
                return child;
            }

            var stdin = info.RedirectStandardInput ? Pipe(true, out parentIn) : Nul();
            var stdout = info.RedirectStandardOutput ? Pipe(false, out parentOut) : Nul();
            var stderr = info.RedirectStandardError ? Pipe(false, out parentErr) : Nul();

            var handles = new[] { stdin, stdout, stderr }.Distinct().ToArray();
            pinned = GCHandle.Alloc(handles, GCHandleType.Pinned);
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_HANDLE_LIST, pinned.AddrOfPinnedObject(),
                    (IntPtr)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput = stdin;
            si.StartupInfo.hStdOutput = stdout;
            si.StartupInfo.hStdError = stderr;
            si.lpAttributeList = attributes;

            var flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED | (info.CreateNoWindow ? CREATE_NO_WINDOW : 0);
            var env = EnvironmentBlock(info.Environment.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)));
            var folder = string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory;
            var commandLine = new StringBuilder(CommandLine(info.FileName, info.Arguments));
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, true, flags, env, folder, ref si, out var pi))
                throw new Win32Exception();

            Process process;
            try
            {
                process = Process.GetProcessById(pi.dwProcessId);
                _ = process.Handle; // held from now on, so ExitCode and WaitForExit work after it ends
            }
            catch (Exception)
            {
                TerminateProcess(pi.hProcess, 1);
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                throw;
            }
            ResumeThread(pi.hThread);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);

            var input = parentIn is null ? null
                : new StreamWriter(new FileStream(parentIn, FileAccess.Write, 4096, false), new UTF8Encoding(false)) { AutoFlush = true };
            var output = parentOut is null ? null
                : new StreamReader(new FileStream(parentOut, FileAccess.Read, 4096, false), info.StandardOutputEncoding ?? Encoding.UTF8);
            var error = parentErr is null ? null
                : new StreamReader(new FileStream(parentErr, FileAccess.Read, 4096, false), info.StandardErrorEncoding ?? Encoding.UTF8);
            parentIn = parentOut = parentErr = null;
            return new ChildProcess(process, input, output, error);
        }
        finally
        {
            foreach (var h in toClose) CloseHandle(h);
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (pinned.IsAllocated) pinned.Free();
            parentIn?.Dispose();
            parentOut?.Dispose();
            parentErr?.Dispose();
        }
    }

    private static SECURITY_ATTRIBUTES Inheritable() => new()
    {
        nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), bInheritHandle = true,
    };

    // ---- Win32 ----------------------------------------------------------------------------------------------------

    private const int STARTF_USESTDHANDLES = 0x100;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x80000, CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_SUSPENDED = 0x4, CREATE_NO_WINDOW = 0x08000000;
    private const int PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x20002;
    private const uint HANDLE_FLAG_INHERIT = 1;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, string lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, ref SECURITY_ATTRIBUTES lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize,
        IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
