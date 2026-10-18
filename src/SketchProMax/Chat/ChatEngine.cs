using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace SketchProMax.Chat;

/// <summary>
/// One Claude Code process for one conversation: `claude -p --input-format stream-json --output-format stream-json`.
/// It stays running between turns (each user message written to stdin starts a turn) and is resumed with --resume
/// after it ended (New chat, an idle timeout, a crash, Stop that had to kill it, or a Revit restart).
/// Output is read on a background thread and every event is raised on the UI dispatcher it was created on; nothing
/// here ever waits on the process from the UI thread.
/// </summary>
internal sealed class ChatEngine : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly ClaudeCli.Install _install;
    private readonly StreamParser _parser = new();
    private readonly StringBuilder _stderr = new();
    private readonly object _gate = new(), _writeLock = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private bool _disposed;

    public ChatEngine(Dispatcher ui, ClaudeCli.Install install, string? resumeSessionId, string model)
    {
        _ui = ui;
        _install = install;
        ResumedFrom = resumeSessionId;
        Model = model;
    }

    /// <summary>The Claude model this process runs (--model); a different choice needs a new process.</summary>
    public string Model { get; }

    /// <summary>The session this engine was asked to resume, if any.</summary>
    public string? ResumedFrom { get; }

    /// <summary>True once system/init arrived (Claude Code started and read its config).</summary>
    public bool Initialized { get; private set; }

    /// <summary>Raised on the UI thread for every event read.</summary>
    public event Action<ChatEvent>? Event;

    /// <summary>Raised on the UI thread when the process ended, with the end of its error output.</summary>
    public event Action<string>? Exited;

    public bool IsAlive
    {
        get
        {
            lock (_gate)
            {
                try { return _process is { HasExited: false }; }
                catch (InvalidOperationException) { return false; }
            }
        }
    }

    /// <summary>Waits up to <paramref name="ms"/> for the process to end; true when it has. Blocks: call off the UI thread.</summary>
    public bool WaitForExit(int ms)
    {
        Process? process;
        lock (_gate) process = _process;
        try { return process is null || process.WaitForExit(ms); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }

    /// <summary>Starts the process off the UI thread and writes <paramref name="firstLine"/> to it.</summary>
    public Task StartAsync(string firstLine) => Task.Run(() =>
    {
        var info = ClaudeCli.EngineStart(_install, ResumedFrom, Model);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Exited += (_, _) => OnExited();
        process.Start();
        lock (_gate)
        {
            if (_disposed)
            {
                ClaudeCli.KillTree(process);
                return;
            }
            _process = process;
            // UTF-8 without a byte order mark on every framework (on .NET Framework StandardInput uses the console code page).
            _stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }
        new Thread(() => ReadOutput(process)) { IsBackground = true, Name = "Sketch ProMax chat output" }.Start();
        new Thread(() => ReadErrors(process)) { IsBackground = true, Name = "Sketch ProMax chat errors" }.Start();
        Write(firstLine);
    });

    /// <summary>Writes one stream-json line (a user message or a control request) off the UI thread.</summary>
    public Task SendAsync(string line) => Task.Run(() => Write(line));

    private void Write(string line)
    {
        // A big picture can fill the pipe until Claude Code reads it: write outside _gate, so IsAlive never waits on it.
        StreamWriter? stdin;
        lock (_gate) stdin = _stdin;
        if (stdin is null) throw new InvalidOperationException("The chat is not running.");
        lock (_writeLock) stdin.WriteLine(line);
    }

    /// <summary>Asks for the current turn to stop (control_request interrupt). The result event ends the turn.</summary>
    public Task InterruptAsync() => SendAsync(StreamInput.Interrupt("interrupt-" + Guid.NewGuid().ToString("N").Substring(0, 8)));

    /// <summary>The last lines Claude Code wrote to stderr.</summary>
    public string ErrorTail
    {
        get
        {
            lock (_stderr) return _stderr.ToString().Trim();
        }
    }

    private void ReadOutput(Process process)
    {
        try
        {
            var reader = process.StandardOutput;
            var buffer = new char[8192];
            int read;
            // Read in chunks rather than lines: the parser keeps a partial last line until its newline arrives.
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                List<ChatEvent> events;
                lock (_parser) events = _parser.Feed(new string(buffer, 0, read));
                if (events.Count == 0) continue;
                foreach (var e in events)
                    if (e is InitEvent) Initialized = true;
                _ui.BeginInvoke(new Action(() =>
                {
                    foreach (var e in events)
                    {
                        try { Event?.Invoke(e); }
                        catch (Exception) { /* a UI problem with one event must not stop the conversation */ }
                    }
                }));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void ReadErrors(Process process)
    {
        try
        {
            string? line;
            while ((line = process.StandardError.ReadLine()) is not null)
            {
                lock (_stderr)
                {
                    _stderr.AppendLine(line);
                    if (_stderr.Length > 6000) _stderr.Remove(0, _stderr.Length - 4000);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void OnExited()
    {
        // Let the output reader deliver what was already written before saying the process ended.
        Task.Delay(300).ContinueWith(_ =>
        {
            bool disposed;
            lock (_gate) disposed = _disposed;
            if (disposed) return;
            var tail = ErrorTail;
            _ui.BeginInvoke(new Action(() => Exited?.Invoke(tail)));
        });
    }

    /// <summary>Ends the process (and the MCP server it started) without raising <see cref="Exited"/>.</summary>
    public void Dispose()
    {
        Process? process;
        StreamWriter? stdin;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            process = _process;
            stdin = _stdin;
            _stdin = null;
        }
        if (process is null) return;
        // Closing stdin lets an idle Claude Code exit by itself; whatever is left is ended. All off the UI thread.
        Task.Run(() =>
        {
            try
            {
                try { stdin?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                if (!process.WaitForExit(1500)) ClaudeCli.KillTree(process);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        });
    }

    /// <summary>Ends the process at once (Stop that the interrupt didn't answer, Revit closing).</summary>
    public void Kill()
    {
        Process? process;
        lock (_gate)
        {
            _disposed = true;
            process = _process;
            _stdin = null;
        }
        if (process is null) return;
        Task.Run(() =>
        {
            ClaudeCli.KillTree(process);
            process.Dispose();
        });
    }

    /// <summary>Ends the process synchronously (Revit is closing: a background task might not get to run).</summary>
    public void KillNow()
    {
        Process? process;
        lock (_gate)
        {
            _disposed = true;
            process = _process;
            _stdin = null;
        }
        if (process is not null) ClaudeCli.KillTree(process);
    }
}
