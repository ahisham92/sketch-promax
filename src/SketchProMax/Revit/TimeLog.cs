using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SketchProMax.Settings;
using SketchProMax.Tools;

namespace SketchProMax.Revit;

/// <summary>
/// Time saved and working time (AI versus by hand), per model, per user and per day (the arithmetic is in
/// <see cref="TimeMath"/> and <see cref="ActivityClock"/>).
///
/// Counting: tool runs (the bridge and the ribbon) and chat tasks are AI time from start to end, approval waits
/// included; a tool the chat runs is part of the chat's time. Activity (a model change, a selection, a view activation,
/// input while Revit is in front and not minimised) makes the minutes around it active; the rest of an active minute is
/// by-hand time.
///
/// Storage: each user and PC keeps cumulative rows in Extensible Storage on a DataStorage element of their own, and the
/// totals of all users go to four Project Information parameters (made the first time). Everything is written inside
/// the task's own transactions, through a dynamic updater that is quiet outside tasks, so there is no extra undo step;
/// an item batch also writes in a last transaction of its group, which is the one place an element may be borrowed or
/// the parameters made in a workshared model (its failure rolls back only that transaction). Elsewhere, in a
/// workshared model, only elements this user already owns (or just made) are written: anything else waits.
///
/// The PC keeps the same rows per model under %APPDATA%\SketchProMax\TimeLog: the backup, and the queue of what the
/// model doesn't have yet. Rows only grow, so each write simply takes the larger value of every field (TimeSheet):
/// nothing is counted twice, and nothing is lost when a write is rolled back or must wait. Ownership is never requested.
/// </summary>
internal static class TimeLog
{
    // ---- Names and ids that are stored in models: never change them --------------------------------------------------

    private static readonly Guid SchemaGuid = new("4b7f2c19-6d3e-4a85-9e21-c3a0f58d71b6");
    private static readonly Guid UpdaterGuid = new("a2e6d0c4-71f3-4b9a-8c5e-2f0d9b3e6a17");

    /// <summary>The Project Information parameters (shared parameters with fixed GUIDs, so every PC makes the same ones).</summary>
    public static readonly (string Name, Guid Guid, string Help)[] Parameters =
    [
        ("Sketch ProMax time saved (h)", new Guid("6e1c3a52-90b4-4d7f-a3e8-15c27f0b9d41"), "Hours saved by Sketch ProMax tasks in this model, all users (by-hand estimate minus AI time)."),
        ("Working time (h)", new Guid("0d8b4f37-2c61-45e9-b07a-9f3e6c14a582"), "Active working hours in this model, all users: AI time plus by-hand time."),
        ("AI time (h)", new Guid("b39e7a05-4f12-4c8d-9e6b-7a2d0f5c3e18"), "Hours Sketch ProMax tools and chats ran in this model, all users."),
        ("By-hand time (h)", new Guid("5c2a9e64-1b7d-4f30-8d95-e6f1b0a47c23"), "Active hours in this model outside Sketch ProMax tasks, all users."),
    ];

    private static readonly object Gate = new();
    private static readonly ActivityClock Clock = new();
    private static readonly Dictionary<string, Ledger> Ledgers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ParametersTried = new(StringComparer.OrdinalIgnoreCase);
    private static bool _started, _on = true, _writing;
    private static List<TimeRate> _rates = [];
    private static string _user = "", _activeKey = "", _activeTitle = "";
    private static readonly string Pc = Environment.MachineName;
    private static Run? _tool, _chat;
    private static TimeUpdater? _updater;
    private static UIApplication? _ui;
    private static int _lastIdle, _lastInput, _lastSave = Environment.TickCount, _lastSettings = Environment.TickCount;

    /// <summary>The line for the tool run that ended last ("Done in X. By hand: about Y. Saved: Z."), for the ribbon's result.</summary>
    public static string? LastLine { get; private set; }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "TimeLog");

    private sealed class Run
    {
        public string Doc = "", Title = "", Tool = "", Brief = "";
        public bool Modifies, InChat;
        public DateTime Start = DateTime.Now;
        public readonly Stopwatch Watch = Stopwatch.StartNew();
        public ActivityClock.Span? Span;
        public int BatchDone, Added, Modified;
        public readonly List<(string Tool, double Estimate)> Nested = [];
        public DateTime LastWrite = DateTime.MinValue;
    }

    // ---- Start and stop (App.StartCore / StopCore, in a valid API context) -------------------------------------------

    public static void Start(UIApplication app)
    {
        if (_started) return;
        _started = true;
        _ui = app;
        try { _user = app.Application.Username ?? ""; } catch (Exception) { _user = ""; }
        if (_user.Length == 0) _user = Environment.UserName;
        ReadSettings();
        try
        {
            app.Application.DocumentChanged += OnDocumentChanged;
            app.SelectionChanged += OnSelectionChanged;
            app.ViewActivated += OnViewActivated;
        }
        catch (Exception) { /* without the events only tasks are counted */ }
        RevitDispatcher.Idle += OnIdle;
        try { if (app.ActiveUIDocument?.Document is { } doc && Tracked(doc)) SetActive(doc); } catch (Exception) { }
        Register(app);
    }

    public static void Stop(UIApplication app)
    {
        if (!_started) return;
        _started = false;
        RevitDispatcher.Idle -= OnIdle;
        try { app.Application.DocumentChanged -= OnDocumentChanged; } catch (Exception) { }
        try { app.SelectionChanged -= OnSelectionChanged; } catch (Exception) { }
        try { app.ViewActivated -= OnViewActivated; } catch (Exception) { }
        if (_updater is { } updater)
        {
            try { UpdaterRegistry.UnregisterUpdater(updater.GetUpdaterId()); } catch (Exception) { }
            _updater = null;
        }
        Shutdown();
        _ui = null;
    }

    /// <summary>Revit is closing (or the core stopping): what is counted so far goes to the PC log.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            try { Drained(Clock.Flush(DateTime.Now)); } catch (Exception) { }
            SaveAll(force: true);
        }
    }

    private static void Register(UIApplication app)
    {
        try
        {
            if (app.ActiveAddInId is not { } addin) return;
            var updater = new TimeUpdater(addin);
            var id = updater.GetUpdaterId();
            // A core switched to while Revit runs: the previous core's updater may still be registered.
            if (UpdaterRegistry.IsUpdaterRegistered(id)) UpdaterRegistry.UnregisterUpdater(id);
            // Optional: a model opened where Sketch ProMax isn't installed gives no warning.
            UpdaterRegistry.RegisterUpdater(updater, true);
            // Kept at once, so Stop unregisters it even when a trigger below fails.
            _updater = updater;
            var all = new LogicalOrFilter(new ElementIsElementTypeFilter(false), new ElementIsElementTypeFilter(true));
            UpdaterRegistry.AddTrigger(id, all, Element.GetChangeTypeAny());
            UpdaterRegistry.AddTrigger(id, all, Element.GetChangeTypeElementAddition());
            try { UpdaterRegistry.AddTrigger(id, all, Element.GetChangeTypeElementDeletion()); } catch (Exception) { }
        }
        catch (Exception) { /* no updater: item batches still write, and the PC log keeps everything */ }
    }

    /// <summary>Settings were saved: the switch, idle threshold and rates apply from now.</summary>
    public static void ReadSettings()
    {
        try
        {
            var s = AgentSettings.Load();
            lock (Gate)
            {
                _on = s.TimeTracking;
                Clock.Idle = TimeSpan.FromMinutes(Math.Max(1, Math.Min(60, s.IdleMinutes)));
                _rates = s.TimeRates?.ToList() ?? [];
            }
        }
        catch (Exception) { }
    }

    public static bool On => _on;

    // ---- Model keys and the PC ledger ------------------------------------------------------------------------------

    private static readonly Dictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The model's key: the central model's path for a workshared model (every local copy is one model), else its path or title.</summary>
    public static string Key(Document doc)
    {
        var local = (doc.PathName ?? "") + "\n" + doc.Title;
        if (Keys.TryGetValue(local, out var known)) return known;
        var key = string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;
        try
        {
            if (doc.IsWorkshared && doc.GetWorksharingCentralModelPath() is { } central &&
                ModelPathUtils.ConvertModelPathToUserVisiblePath(central) is { Length: > 0 } visible) key = visible;
        }
        catch (Exception) { }
        if (Keys.Count > 200) Keys.Clear();
        return Keys[local] = key;
    }

    private static bool Tracked(Document doc)
    {
        try { return doc.IsValidObject && !doc.IsFamilyDocument && !doc.IsLinked; }
        catch (Exception) { return false; }
    }

    /// <summary>This PC's rows for one model, read from its file the first time.</summary>
    private sealed class Ledger
    {
        public string Key = "", Title = "";
        public TimeSheet Sheet = new();
        public bool Dirty;

        public string File => Path.Combine(Folder, "models", Hash(Key) + ".json");

        private static string Hash(string key)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key.ToLowerInvariant()));
            return string.Concat(bytes.Take(10).Select(b => b.ToString("x2")));
        }
    }

    private sealed class LedgerFile
    {
        public string Model { get; set; } = "";
        public string Title { get; set; } = "";
        public string User { get; set; } = "";
        public string Pc { get; set; } = "";
        public string Rows { get; set; } = "";
        public DateTime Updated { get; set; }
    }

    private static Ledger LedgerFor(string key, string title)
    {
        if (Ledgers.TryGetValue(key, out var ledger))
        {
            if (title.Length > 0) ledger.Title = title;
            return ledger;
        }
        ledger = new Ledger { Key = key, Title = title, Sheet = new TimeSheet(_user, Pc) };
        try
        {
            if (SafeFile.Read(ledger.File) is { } text && JsonSerializer.Deserialize<LedgerFile>(text) is { } saved &&
                string.Equals(saved.User, _user, StringComparison.OrdinalIgnoreCase))
                ledger.Sheet.MergeMax(TimeSheet.Parse(_user, Pc, saved.Rows));
        }
        catch (Exception) { /* a damaged file: start again; the model still has what was written */ }
        return Ledgers[key] = ledger;
    }

    private static void SaveAll(bool force)
    {
        if (!force && unchecked(Environment.TickCount - _lastSave) < 60_000) return;
        _lastSave = Environment.TickCount;
        foreach (var ledger in Ledgers.Values.Where(l => l.Dirty))
        {
            try
            {
                SafeFile.Write(ledger.File, JsonSerializer.Serialize(new LedgerFile
                {
                    Model = ledger.Key, Title = ledger.Title, User = _user, Pc = Pc, Rows = ledger.Sheet.Serialize(), Updated = DateTime.Now,
                }, new JsonSerializerOptions { WriteIndented = true }));
                ledger.Dirty = false;
            }
            catch (Exception) { /* tried again at the next save */ }
        }
    }

    /// <summary>One line per task in the month's log, the readable backup next to the per-model rows.</summary>
    private static void Log(object entry)
    {
        try
        {
            var file = Path.Combine(Folder, $"tasks-{DateTime.Now:yyyy-MM}.jsonl");
            Directory.CreateDirectory(Folder);
            File.AppendAllText(file, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch (Exception) { /* logging must never break a task */ }
    }

    public static string LogFile => Path.Combine(Folder, $"tasks-{DateTime.Now:yyyy-MM}.jsonl");

    private static void Drained(List<(string Doc, string Day, double Seconds)> minutes)
    {
        foreach (var (doc, day, seconds) in minutes)
        {
            var ledger = LedgerFor(doc, "");
            ledger.Sheet.Add(day, TimeMath.HandTool, byHand: seconds);
            ledger.Dirty = true;
        }
    }

    // ---- Activity ----------------------------------------------------------------------------------------------------

    private static void SetActive(Document doc)
    {
        _activeKey = Key(doc);
        _activeTitle = doc.Title;
    }

    private static void Active(Document? doc)
    {
        // The field test's own changes and views are nobody's work (its time probes write their own records).
        if (!_on || doc is null || StartupTimings.TestRunning || !Tracked(doc)) return;
        lock (Gate)
        {
            SetActive(doc);
            Clock.Activity(_activeKey, DateTime.Now);
        }
    }

    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        try
        {
            var doc = e.GetDocument();
            // The self-test's changes are rolled back and are nobody's work.
            if (!_on || SelfTest.IsRunning || !Tracked(doc)) return;
            if (_tool is { } run && string.Equals(run.Doc, Key(doc), StringComparison.OrdinalIgnoreCase))
            {
                // A tool's own changes: items for its by-hand estimate, not activity. Our own records are left out.
                var info = doc.ProjectInformation?.Id;
                bool Ours(ElementId id) => id == info || doc.GetElement(id) is DataStorage;
                run.Added += e.GetAddedElementIds().Count(id => !Ours(id));
                run.Modified += e.GetModifiedElementIds().Count(id => !Ours(id));
                return;
            }
            if (_tool is not null || _chat is not null) return;
            Active(doc);
        }
        catch (Exception) { }
    }

    private static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try { Active(e.GetDocument()); } catch (Exception) { }
    }

    private static void OnViewActivated(object? sender, ViewActivatedEventArgs e)
    {
        try { Active(e.Document); } catch (Exception) { }
    }

    /// <summary>
    /// At Revit's idle moments (the dispatcher's Idling): input since the last look, with Revit in front and not
    /// minimised, is activity in the active model. Also hands finished minutes to the ledgers and saves them now and then.
    /// </summary>
    private static void OnIdle(UIApplication app)
    {
        if (!_started || unchecked(Environment.TickCount - _lastIdle) < 2000) return;
        _lastIdle = Environment.TickCount;
        try
        {
            if (unchecked(Environment.TickCount - _lastSettings) > 60_000)
            {
                _lastSettings = Environment.TickCount;
                ReadSettings();
            }
            if (!_on) return;
            var input = LastInput();
            var fresh = input != 0 && input != _lastInput;
            _lastInput = input;
            if (fresh && RevitInFront() && app.ActiveUIDocument?.Document is { } doc && Tracked(doc) && _tool is null && _chat is null) Active(doc);
            lock (Gate)
            {
                Drained(Clock.Drain(DateTime.Now));
                SaveAll(force: false);
            }
        }
        catch (Exception) { }
    }

    private static bool RevitInFront()
    {
        try
        {
            var main = App.MainWindow;
            if (main != IntPtr.Zero && IsIconic(main)) return false;
            var front = GetForegroundWindow();
            if (front == IntPtr.Zero) return false;
            GetWindowThreadProcessId(front, out var pid);
            return pid == (uint)Process.GetCurrentProcess().Id;
        }
        catch (Exception) { return false; }
    }

    private static int LastInput()
    {
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            return GetLastInputInfo(ref info) ? unchecked((int)info.dwTime) : 0;
        }
        catch (Exception) { return 0; }
    }

    // ---- Tasks -------------------------------------------------------------------------------------------------------

    /// <summary>A tool starts (the bridge or the ribbon), before its approval. Call on Revit's thread.</summary>
    public static void BeginTool(UIApplication app, IRevitTool tool, bool fromChat)
    {
        LastLine = null;
        if (!_on) return;
        try
        {
            if (app.ActiveUIDocument?.Document is not { } doc || !Tracked(doc)) return;
            lock (Gate)
            {
                SetActive(doc);
                // A run that never ended (it can't happen through the bridge or the ribbon): its span would hold the clock.
                if (_tool is { } stale) EndSpan(stale);
                var run = new Run { Doc = _activeKey, Title = doc.Title, Tool = tool.Name, Modifies = tool.ModifiesModel, InChat = fromChat && _chat is not null };
                if (!run.InChat) run.Span = Clock.TaskStart(run.Start);
                _tool = run;
                LedgerFor(run.Doc, run.Title);
            }
        }
        catch (Exception)
        {
            if (_tool is { } run) lock (Gate) EndSpan(run);
            _tool = null;
        }
    }

    /// <summary>Ends a run's span on the activity clock, if it has one still open (a span left open stops by-hand counting).</summary>
    private static void EndSpan(Run run)
    {
        try { if (run.Span is { } span) Clock.TaskEnd(span, run.Doc, DateTime.Now); }
        catch (Exception) { }
    }

    /// <summary>
    /// The tool ended: records it and returns the line to show (null when nothing was counted). Null result: it never
    /// ran (rejected, no model); only the wait is AI time. Does nothing once the run is recorded.
    /// </summary>
    public static string? EndTool(ToolResult? result)
    {
        var run = _tool;
        _tool = null;
        if (run is null) return null;
        try
        {
            lock (Gate)
            {
                var took = run.Watch.Elapsed.TotalSeconds;
                var units = TimeMath.Units(run.BatchDone, run.Added, run.Modified);
                var failed = result is null || result.IsError;
                var runs = result is null ? 0 : 1;
                var estimate = TimeMath.EstimateMinutes(TimeMath.RateFor(run.Tool, run.Modifies, _rates), units, failed) * 60;
                var now = DateTime.Now;
                var ledger = LedgerFor(run.Doc, run.Title);
                double saved = 0;
                if (run.InChat && _chat is { } chat)
                {
                    // Part of a chat task: its time is the chat's; the saving is shared out when the chat ends.
                    chat.Nested.Add((run.Tool, estimate));
                    ledger.Sheet.Add(TimeMath.Day(now), run.Tool, estimate: estimate, runs: runs, units: units);
                }
                else
                {
                    saved = TimeMath.Saved(estimate, took);
                    ledger.Sheet.Add(TimeMath.Day(now), run.Tool, ai: took, estimate: estimate, saved: saved, runs: runs, units: units);
                    if (run.Span is { } span) Clock.TaskEnd(span, run.Doc, now);
                }
                ledger.Dirty = true;
                Log(new
                {
                    time = now.ToString("s"), model = run.Title, key = run.Doc, user = _user, pc = Pc, tool = run.Tool, inChat = run.InChat,
                    tookSeconds = Math.Round(took, 1), units, byHandMinutes = Math.Round(estimate / 60, 1), savedMinutes = Math.Round(saved / 60, 1), ok = !failed,
                });
                SaveAll(force: true);
                LastLine = run.InChat ? null : estimate > 0 ? TimeMath.Line(took, estimate) : $"Done in {TimeMath.Duration(took)}.";
                return LastLine;
            }
        }
        catch (Exception)
        {
            lock (Gate) EndSpan(run);
            return null;
        }
    }

    /// <summary>A chat task starts (the message is sent). Any thread; counted for the model last active in Revit. <paramref name="brief"/>: the approved brief, kept in the task log.</summary>
    public static void BeginChat(string brief = "")
    {
        if (!_on) return;
        lock (Gate)
        {
            if (_activeKey.Length == 0) return;
            // A chat task that never ended (the pane was cleared mid-task): its span would stop by-hand counting for good.
            if (_chat is { } stale) EndSpan(stale);
            var run = new Run { Doc = _activeKey, Title = _activeTitle, Tool = TimeMath.ChatTool, Brief = brief };
            run.Span = Clock.TaskStart(run.Start);
            _chat = run;
        }
    }

    /// <summary>The chat task ended: records it and returns the line to show when its tools saved anything, else null.</summary>
    public static string? EndChat()
    {
        lock (Gate)
        {
            var run = _chat;
            _chat = null;
            if (run is null) return null;
            try
            {
                var took = run.Watch.Elapsed.TotalSeconds;
                var now = DateTime.Now;
                var day = TimeMath.Day(now);
                var ledger = LedgerFor(run.Doc, run.Title);
                ledger.Sheet.Add(day, TimeMath.ChatTool, ai: took, runs: 1);
                var shares = TimeMath.SplitChat(took, run.Nested.Select(n => n.Estimate).ToList());
                for (var i = 0; i < run.Nested.Count; i++) ledger.Sheet.Add(day, run.Nested[i].Tool, saved: shares[i]);
                ledger.Dirty = true;
                if (run.Span is { } span) Clock.TaskEnd(span, run.Doc, now);
                var estimate = run.Nested.Sum(n => n.Estimate);
                Log(new
                {
                    time = now.ToString("s"), model = run.Title, key = run.Doc, user = _user, pc = Pc, tool = TimeMath.ChatTool,
                    tookSeconds = Math.Round(took, 1), tools = run.Nested.Select(n => n.Tool).ToList(),
                    byHandMinutes = Math.Round(estimate / 60, 1), savedMinutes = Math.Round(shares.Sum() / 60, 1), brief = run.Brief,
                });
                SaveAll(force: true);
                return estimate > 0 ? TimeMath.Line(took, estimate) : null;
            }
            catch (Exception)
            {
                EndSpan(run);
                return null;
            }
        }
    }

    private static bool Running(string key) =>
        _chat is { } c && string.Equals(c.Doc, key, StringComparison.OrdinalIgnoreCase) ||
        _tool is { } t && string.Equals(t.Doc, key, StringComparison.OrdinalIgnoreCase);

    /// <summary>The running task's time so far, so a write inside the task already carries it (rows only grow).</summary>
    private static void AddRunning(TimeSheet sheet, string key)
    {
        var now = DateTime.Now;
        if (_chat is { } chat && string.Equals(chat.Doc, key, StringComparison.OrdinalIgnoreCase))
            sheet.Add(TimeMath.Day(now), TimeMath.ChatTool, ai: chat.Watch.Elapsed.TotalSeconds);
        else if (_tool is { InChat: false } tool && string.Equals(tool.Doc, key, StringComparison.OrdinalIgnoreCase))
            sheet.Add(TimeMath.Day(now), tool.Tool, ai: tool.Watch.Elapsed.TotalSeconds);
    }

    // ---- Writing to the model ----------------------------------------------------------------------------------------

    /// <summary>From the updater, inside a transaction of the running tool: write when this is the tool's model.</summary>
    private static void FromUpdater(Document doc)
    {
        if (!_on || _writing || _tool is not { } run || !Tracked(doc)) return;
        if (!string.Equals(run.Doc, Key(doc), StringComparison.OrdinalIgnoreCase)) return;
        // Many small transactions (an item batch): once in a while is enough, the last write wins anyway.
        if (DateTime.Now - run.LastWrite < TimeSpan.FromSeconds(15)) return;
        run.LastWrite = DateTime.Now;
        // No parameter bindings here: changing them is not something an updater may do (and it would swap the
        // user's shared parameter file mid-transaction); they are made in an item batch's own transaction.
        lock (Gate) Write(doc, borrow: false, bindings: false);
    }

    /// <summary>
    /// An item batch finished its items (ItemBatch, inside its transaction group): count them for the by-hand estimate,
    /// and write in one more transaction of the group, so it stays one undo step. Here elements nobody owns may be
    /// borrowed and the parameters made; a failure rolls back only this transaction.
    /// </summary>
    public static void InBatch(Document doc, string name, int done)
    {
        try
        {
            if (!_on || _tool is not { } run || !Tracked(doc) || !string.Equals(run.Doc, Key(doc), StringComparison.OrdinalIgnoreCase)) return;
            run.BatchDone += done;
            if (doc.IsModifiable || doc.IsReadOnly) return;
            using var t = new Transaction(doc, name);
            var options = t.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new RollBackOnError());
            options.SetClearAfterRollback(true);
            options.SetForcedModalHandling(false);
            t.SetFailureHandlingOptions(options);
            FailureGuard.Muted++;
            try
            {
                t.Start();
                bool changed;
                lock (Gate) changed = Write(doc, borrow: true, bindings: true);
                if (changed) t.Commit();
                else t.RollBack();
                run.LastWrite = DateTime.Now;
            }
            finally
            {
                FailureGuard.Muted--;
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            }
        }
        catch (Exception) { /* the batch's own work stays; the PC keeps the time */ }
    }

    private sealed class RollBackOnError : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var error = false;
            foreach (var f in accessor.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(f);
                else error = true;
            }
            return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }

    private const string FUser = "User", FPc = "Pc", FRows = "Rows";

    private static Schema TimeSchema()
    {
        if (Schema.Lookup(SchemaGuid) is { } known) return known;
        var b = new SchemaBuilder(SchemaGuid);
        b.SetSchemaName("SketchProMaxTimeLog");
        b.SetVendorId("SPMX");
        b.SetReadAccessLevel(AccessLevel.Public);
        b.SetWriteAccessLevel(AccessLevel.Public);
        b.SetDocumentation("Sketch ProMax working time and time saved, one user and PC per element: day, tool, AI s, by-hand s, estimate s, saved s, runs, items.");
        b.AddSimpleField(FUser, typeof(string));
        b.AddSimpleField(FPc, typeof(string));
        b.AddSimpleField(FRows, typeof(string));
        return b.Finish();
    }

    private sealed record Holder(DataStorage Element, TimeSheet Sheet, string Text);

    /// <summary>Every user's and PC's rows stored in the model.</summary>
    private static List<Holder> Holders(Document doc)
    {
        var list = new List<Holder>();
        if (Schema.Lookup(SchemaGuid) is not { } schema) return list;
        foreach (var ds in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>())
        {
            try
            {
                var entity = ds.GetEntity(schema);
                if (!entity.IsValid()) continue;
                var text = entity.Get<string>(FRows) ?? "";
                list.Add(new Holder(ds, TimeSheet.Parse(entity.Get<string>(FUser) ?? "", entity.Get<string>(FPc) ?? "", text), text));
            }
            catch (Exception) { }
        }
        return list;
    }

    private static bool Mine(TimeSheet s) =>
        string.Equals(s.User, _user, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Pc, Pc, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this element can be written now without asking anyone. In the tool's own transactions (borrow false)
    /// only an element this user owns, or one just made: borrowing could fail and roll the tool's work back. In an item
    /// batch's last transaction (borrow true) also one nobody owns and that is current with the central model.
    /// </summary>
    private static bool Writable(Document doc, ElementId id, bool borrow)
    {
        if (OwnedElsewhereForTest is not null) return false;
        if (!doc.IsWorkshared) return true;
        try
        {
            var status = WorksharingUtils.GetCheckoutStatus(doc, id);
            var updates = WorksharingUtils.GetModelUpdatesStatus(doc, id);
            if (status == CheckoutStatus.OwnedByOtherUser || updates is ModelUpdatesStatus.UpdatedInCentral or ModelUpdatesStatus.DeletedInCentral) return false;
            if (status == CheckoutStatus.OwnedByCurrentUser || updates == ModelUpdatesStatus.NotYetInCentral) return true;
            return borrow && updates == ModelUpdatesStatus.CurrentWithCentral;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Writes this PC's rows (and the running task's time so far) to the model, and all users' totals to the Project
    /// Information parameters. Needs an open transaction. Returns true when something in the model changed.
    /// <paramref name="bindings"/>: the four parameters may be made here (never from the updater).
    /// </summary>
    private static bool Write(Document doc, bool borrow, bool bindings)
    {
        if (_writing || !doc.IsModifiable) return false;
        _writing = true;
        try
        {
            var key = Key(doc);
            var ledger = LedgerFor(key, doc.Title);
            var holders = Holders(doc);
            var ours = holders.Where(h => Mine(h.Sheet)).ToList();
            var stored = TimeSheet.Combine(ours.Select(h => h.Sheet)).FirstOrDefault();
            // The model may know more than this PC (its file was lost): take it back, but only between tasks: during one
            // the model may hold that task's time so far, which the ledger only gets, whole, when the task ends.
            if (stored is not null && !Running(key) && ledger.Sheet.MergeMax(stored)) ledger.Dirty = true;
            var mine = ledger.Sheet.Copy();
            AddRunning(mine, key);
            if (stored is not null) mine.MergeMax(stored);
            var text = mine.Serialize();
            var changed = false;

            var schema = TimeSchema();
            var target = ours.FirstOrDefault(h => h.Text == text);
            if (target is null && (stored is null || stored.IsBehind(mine)))
            {
                // Into one of ours that can be written now; else (borrowed elsewhere, or not owned while inside the tool's
                // own transaction) a new element of our own. Readers take the larger value of each field over all of them.
                var element = ours.FirstOrDefault(h => Writable(doc, h.Element.Id, borrow))?.Element ?? DataStorage.Create(doc);
                var entity = new Entity(schema);
                entity.Set(FUser, _user);
                entity.Set(FPc, Pc);
                entity.Set(FRows, text);
                element.SetEntity(entity);
                changed = true;
                target = new Holder(element, mine, text);
            }
            if (borrow && target is not null)
            {
                // Copies left by earlier waits go when they can, now that one element holds everything.
                var extra = ours.Where(h => h.Element.Id != target.Element.Id && Writable(doc, h.Element.Id, true)).Select(h => h.Element.Id).ToList();
                if (extra.Count > 0)
                {
                    try { doc.Delete(extra); changed = true; }
                    catch (Exception) { }
                }
            }

            var everyone = TimeSheet.Combine(holders.Where(h => !Mine(h.Sheet)).Select(h => h.Sheet).Append(mine));
            changed |= SetTotals(doc, TimeTotals.Of(everyone.SelectMany(s => s.Rows.Values)), borrow, bindings, key);
            return changed;
        }
        finally { _writing = false; }
    }

    private static bool SetTotals(Document doc, TimeTotals totals, bool borrow, bool bindings, string key)
    {
        var info = doc.ProjectInformation;
        if (info is null) return false;
        var changed = false;
        // Made outside the updater (an item batch's own transaction), at most once per model and session: a try that
        // fails (bindings refused, or the transaction rolled back) isn't repeated with every batch.
        if (bindings && Parameters.Any(p => info.get_Parameter(p.Guid) is null) && (borrow || !doc.IsWorkshared)
            && Writable(doc, info.Id, borrow) && ParametersTried.Add(key))
            changed |= MakeParameters(doc);
        if (!Writable(doc, info.Id, borrow)) return changed;
        double[] values = [TimeMath.Hours(totals.Saved), TimeMath.Hours(totals.Working), TimeMath.Hours(totals.Ai), TimeMath.Hours(totals.ByHand)];
        for (var i = 0; i < Parameters.Length; i++)
        {
            try
            {
                if (info.get_Parameter(Parameters[i].Guid) is not { IsReadOnly: false, StorageType: StorageType.Double } p) continue;
                if (Math.Abs(p.AsDouble() - values[i]) < 0.005) continue;
                p.Set(values[i]);
                changed = true;
            }
            catch (Exception) { }
        }
        return changed;
    }

    /// <summary>Binds the four parameters to Project Information (number, Data group), from a throwaway shared parameter file.</summary>
    private static bool MakeParameters(Document doc)
    {
        var revit = doc.Application;
        var original = revit.SharedParametersFilename;
        var file = Path.Combine(Path.GetTempPath(), "SketchProMax", $"TimeParameters-{Guid.NewGuid():N}.txt");
        var made = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "", new UTF8Encoding(false));
            revit.SharedParametersFilename = file;
            var defFile = revit.OpenSharedParameterFile();
            if (defFile is null) return false;
            var group = defFile.Groups.Create("Sketch ProMax");
            var cats = revit.Create.NewCategorySet();
            cats.Insert(Category.GetCategory(doc, BuiltInCategory.OST_ProjectInformation));
            foreach (var (name, guid, help) in Parameters)
            {
                try
                {
                    if (doc.ProjectInformation.get_Parameter(guid) is not null) continue;
                    Definition def = SharedParameterElement.Lookup(doc, guid)?.GetDefinition()
                        ?? group.Definitions.Create(new ExternalDefinitionCreationOptions(name, SpecTypeId.Number) { GUID = guid, Description = help, UserModifiable = false });
                    made |= doc.ParameterBindings.Insert(def, revit.Create.NewInstanceBinding(cats), GroupTypeId.Data);
                }
                catch (Exception) { /* a parameter of that name already exists, or bindings can't change here */ }
            }
        }
        catch (Exception) { }
        finally
        {
            try { revit.SharedParametersFilename = original ?? ""; } catch (Exception) { }
            try { File.Delete(file); } catch (Exception) { }
        }
        return made;
    }

    private sealed class TimeUpdater(AddInId addin) : IUpdater
    {
        private readonly UpdaterId _id = new(addin, UpdaterGuid);

        public void Execute(UpdaterData data)
        {
            try { FromUpdater(data.GetDocument()); }
            catch (Exception) { /* never fail the tool's transaction */ }
        }

        public UpdaterId GetUpdaterId() => _id;
        public ChangePriority GetChangePriority() => ChangePriority.Annotations;
        public string GetUpdaterName() => "Sketch ProMax time log";
        public string GetAdditionalInformation() => "Records Sketch ProMax working time and time saved in the model during Sketch ProMax tasks. Optional.";
    }

    // ---- Reading -----------------------------------------------------------------------------------------------------

    /// <summary>The summary of one model: every user's rows in it, with this PC's queued time added. Call on Revit's thread.</summary>
    public static TimeReport Report(Document doc)
    {
        var key = Key(doc);
        lock (Gate)
        {
            var holders = Holders(doc);
            var ledger = LedgerFor(key, doc.Title);
            var mine = ledger.Sheet.Copy();
            AddRunning(mine, key);
            var stored = TimeSheet.Combine(holders.Where(h => Mine(h.Sheet)).Select(h => h.Sheet)).FirstOrDefault() ?? new TimeSheet(_user, Pc);
            string? queued = null;
            if (stored.IsBehind(mine))
                queued = "Some of your time from this PC isn't in the model yet. It is kept on this PC and added to the model with one of your next " +
                         "Sketch ProMax tasks" + (ProjectInfoHolder(doc) is { } who ? $" (the time record is borrowed by {who})." : ".");
            return new TimeReport
            {
                Model = ProjectPack.ModelName(doc),
                Sheets = TimeSheet.Combine(holders.Select(h => h.Sheet).Append(mine)),
                Queued = queued,
            };
        }
    }

    /// <summary>Who else holds Project Information in a workshared model, or null.</summary>
    private static string? ProjectInfoHolder(Document doc)
    {
        if (OwnedElsewhereForTest is { } test) return test;
        if (!doc.IsWorkshared || doc.ProjectInformation is not { } info) return null;
        try
        {
            return WorksharingUtils.GetCheckoutStatus(doc, info.Id, out var owner) == CheckoutStatus.OwnedByOtherUser && owner.Length > 0 ? owner : null;
        }
        catch (Exception) { return null; }
    }

    // ---- For the field test ------------------------------------------------------------------------------------------

    /// <summary>
    /// For the field test: while set (to a user name), every element counts as owned by that user, as when a colleague
    /// has borrowed Project Information and the time records in a workshared model. One Revit session has only one
    /// user, so the field test can't make that state for real. Always null outside the field test.
    /// </summary>
    internal static string? OwnedElsewhereForTest;

    /// <summary>
    /// For the field test, before it closes its test project: forgets this PC's rows for that model and deletes their
    /// file, so the probes leave records only in the throwaway project (and the month's task log, marked simulated).
    /// </summary>
    internal static void ForgetForTest(Document doc)
    {
        lock (Gate)
        {
            var key = Key(doc);
            var file = (Ledgers.TryGetValue(key, out var ledger) ? ledger : new Ledger { Key = key }).File;
            Ledgers.Remove(key);
            ParametersTried.Remove(key);
            try { File.Delete(file); } catch (Exception) { }
        }
    }

    /// <summary>For the field test: this user's and PC's row for (day, tool) as stored in the model's Extensible Storage, or null.</summary>
    internal static TimeRow? StoredRowForTest(Document doc, string day, string tool)
    {
        lock (Gate)
        {
            var stored = TimeSheet.Combine(Holders(doc).Where(h => Mine(h.Sheet)).Select(h => h.Sheet)).FirstOrDefault();
            return stored is not null && stored.Rows.TryGetValue(day + "\t" + tool, out var row) ? row : null;
        }
    }

    /// <summary>For the field test: what an item batch's last transaction does, on its own (writes what waited). Returns what happened.</summary>
    internal static string WriteForTest(Document doc)
    {
        if (doc.IsModifiable || doc.IsReadOnly) return "not written: a transaction is open or the model is read-only";
        using var t = new Transaction(doc, "Sketch ProMax: time log test");
        t.Start();
        bool changed;
        lock (Gate) changed = Write(doc, borrow: true, bindings: true);
        return t.Commit() == TransactionStatus.Committed ? changed ? "written" : "nothing to write" : "not written: the transaction was rolled back";
    }

    /// <summary>
    /// For the field test: replays a scripted session on a private clock (no real time passes) for this model, records
    /// it like real work (the PC log included) and writes it to the model: in the open transaction when there is one
    /// (the tool's own rules), else in a transaction of its own (the item batch's rules). Session: activity every 30 s
    /// for <paramref name="activeMinutes"/>, then <paramref name="idleMinutes"/> without input (more than the idle
    /// threshold, so not counted), then a task of <paramref name="aiMinutes"/> (activity during it ignored), then two
    /// minutes of activity bridged from the task's end. Expected: by-hand = activeMinutes + 2, AI = aiMinutes.
    /// </summary>
    public static object SimulateForTest(Document doc, int activeMinutes = 6, int idleMinutes = 8, int aiMinutes = 3, int units = 4, string tool = "create_sheets")
    {
        activeMinutes = Math.Max(1, Math.Min(60, activeMinutes));
        aiMinutes = Math.Max(1, Math.Min(60, aiMinutes));
        var clock = new ActivityClock { Idle = Clock.Idle };
        idleMinutes = Math.Max((int)Math.Ceiling(clock.Idle.TotalMinutes) + 1, idleMinutes);
        var key = Key(doc);
        var t0 = DateTime.Today; // a whole minute; the private clock needs no real time
        for (var i = 0; i < activeMinutes * 2; i++) clock.Activity(key, t0.AddSeconds(30 * i));
        var start = t0.AddMinutes(activeMinutes + idleMinutes);
        var span = clock.TaskStart(start);
        clock.Activity(key, start.AddSeconds(20)); // during the task: AI time, not by hand
        var end = start.AddMinutes(aiMinutes);
        clock.TaskEnd(span, key, end);
        clock.Activity(key, end.AddSeconds(30));
        clock.Activity(key, end.AddSeconds(90));
        var drained = clock.Flush(end.AddMinutes(1));
        var byHand = drained.Where(d => d.Doc == key).Sum(d => d.Seconds);
        var ai = aiMinutes * 60.0;
        var estimate = TimeMath.EstimateMinutes(TimeMath.RateFor(tool, true, _rates), units) * 60;
        var saved = TimeMath.Saved(estimate, ai);
        var line = TimeMath.Line(ai, estimate);
        var day = TimeMath.Day(DateTime.Now);

        string? written = null;
        lock (Gate)
        {
            var ledger = LedgerFor(key, doc.Title);
            ledger.Sheet.Add(day, TimeMath.HandTool, byHand: byHand);
            ledger.Sheet.Add(day, tool, ai: ai, estimate: estimate, saved: saved, runs: 1, units: units);
            ledger.Dirty = true;
            Log(new { time = DateTime.Now.ToString("s"), model = doc.Title, key, user = _user, pc = Pc, tool, simulated = true,
                      tookSeconds = ai, units, byHandMinutes = Math.Round(estimate / 60, 1), savedMinutes = Math.Round(saved / 60, 1), byHandWorkedSeconds = byHand });
            SaveAll(force: true);
        }
        try
        {
            if (doc.IsModifiable)
            {
                lock (Gate) written = Write(doc, borrow: false, bindings: true) ? "written in the open transaction" : "nothing to write, or it waits (elements owned elsewhere)";
            }
            else if (!doc.IsReadOnly)
            {
                using var t = new Transaction(doc, "Sketch ProMax: time log test");
                t.Start();
                bool changed;
                lock (Gate) changed = Write(doc, borrow: true, bindings: true);
                written = t.Commit() == TransactionStatus.Committed && changed ? "written in its own transaction" : "nothing written (it waits)";
            }
            else written = "the model is read-only";
        }
        catch (Exception ex) { written = "not written: " + ex.Message; }

        var info = doc.ProjectInformation;
        var report = Report(doc);
        return new
        {
            ok = Math.Abs(byHand - (activeMinutes + 2) * 60) < 0.5 && drained.All(d => d.Doc == key),
            expected = new { byHandSeconds = (activeMinutes + 2) * 60, aiSeconds = ai, idleSecondsCounted = 0 },
            counted = new { byHandSeconds = byHand, aiSeconds = ai, estimateSeconds = estimate, savedSeconds = saved },
            line,
            written,
            workshared = doc.IsWorkshared,
            queued = report.Queued,
            parameters = Parameters.Select(p => new { p.Name, value = info?.get_Parameter(p.Guid)?.AsDouble() }).ToList(),
            modelTotals = new { report.Totals.Working, report.Totals.Ai, report.Totals.ByHand, report.Totals.Saved },
            log = LogFile,
        };
    }

    // ---- Win32 -------------------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}
