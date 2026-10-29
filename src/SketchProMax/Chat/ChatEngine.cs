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
    private readonly Func<ProcessStartInfo>? _start;

    /// <param name="start">How to start the process: the main chat's engine when null; a parallel job's own (ClaudeCli.JobStart).</param>
    /// <param name="coordinator">The main chat's engine: true for a chat With coordinator (it gets chat_jobs and the
    /// coordinator's instructions), false for a Simple chat.</param>
    /// <param name="job">The parallel job this process is (its MCP config's job header), or null for the main chat.</param>
    public ChatEngine(Dispatcher ui, ClaudeCli.Install install, string? resumeSessionId, string model, Func<ProcessStartInfo>? start = null, bool coordinator = true, string effort = "", string? job = null, string? chatKey = null)
    {
        ChatKey = chatKey;
        Job = job;
        Effort = effort;
        _ui = ui;
        _install = install;
        ResumedFrom = resumeSessionId;
        Model = model;
        _start = start;
        Coordinator = coordinator;
    }

    /// <summary>The parallel job this process is, or null for the main chat.</summary>
    public string? Job { get; }

    /// <summary>The main chat's own key (JobBoard.ChatKey) its calls carry, or null (a job's process, or a chat without an id).</summary>
    public string? ChatKey { get; }

    /// <summary>The main chat's kind this process was started for (ChatTypes); switching the kind needs a new process.</summary>
    public bool Coordinator { get; }

    /// <summary>The effort this process runs at (--effort; "" for Claude Code's default); a different one needs a new process.</summary>
    public string Effort { get; }

    /// <summary>The Claude model this process runs (--model); a different choice needs a new process.</summary>
    public string Model { get; }

    /// <summary>Where this process reaches the Revit tools (ClaudeCli.McpAddress when it started).</summary>
    public string McpAddress { get; private set; } = "";

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
        var info = _start?.Invoke() ?? ClaudeCli.EngineStart(_install, ResumedFrom, Model, Coordinator, Effort, ChatKey);
        McpAddress = ClaudeCli.McpAddress;
        var started = DateTime.UtcNow;
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        // Raised on a thread-pool thread: an exception there would close Revit.
        process.Exited += (_, _) => { try { OnExited(); } catch (Exception) { } };
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
        WaitForTools(process, started);
        Write(firstLine);
    });

    /// <summary>How long a new process may take to fetch the Revit tools before its first message goes anyway.</summary>
    internal const int ToolsWaitMs = 15000;

    /// <summary>
    /// Set when a process didn't fetch the Revit tools within <see cref="ToolsWaitMs"/> (a Claude Code that connects only
    /// once a message arrives, or a very slow start): later processes in this Revit session don't wait at all.
    /// </summary>
    internal static volatile bool ToolsWaitUseless;

    /// <summary>
    /// Claude Code connects to its MCP servers in the background and starts the first turn without waiting: a turn that
    /// begins with a Revit call (after a brief, which also starts a new process for the model and effort it picked) failed
    /// with "No such tool available" and had to try again (bug log row 40). So the first message waits until the chat's
    /// connection inside Revit has sent this process the tool list, plus a moment for Claude Code to take it in. Nothing
    /// to wait for with the connector program (it is outside Revit). Blocks: called from StartAsync's thread-pool task.
    /// </summary>
    private void WaitForTools(Process process, DateTime started)
    {
        if (ToolsWaitUseless || Bridge.ChatMcp.Current is not { } http || http.Url != McpAddress || McpAddress.Length == 0) return;
        var until = started.AddMilliseconds(ToolsWaitMs);
        while (!http.ToolsListedSince(Job ?? ChatKey, started))
        {
            bool gone;
            lock (_gate) gone = _disposed;
            try { gone |= process.HasExited; } catch (InvalidOperationException) { gone = true; }
            if (gone) return;
            if (DateTime.UtcNow >= until)
            {
                ToolsWaitUseless = true;
                return;
            }
            Thread.Sleep(100);
        }
        Thread.Sleep(300);
    }

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
        // Its own thread: anything else escaping it (the parser, a dispatcher that shut down) would close Revit.
        catch (Exception) { }
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
        catch (Exception) { /* its own thread: nothing may escape */ }
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
            _ui.BeginInvoke(new Action(() =>
            {
                try { Exited?.Invoke(tail); }
                catch (Exception) { /* a UI problem must not reach Revit's dispatcher */ }
            }));
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
