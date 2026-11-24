using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PadForge.Services.Automation;

namespace PadForge.Services.Plugins
{
    /// <summary>Where a plugin stands.</summary>
    internal enum PluginStatus
    {
        /// <summary>Its switch is off, or Plugins is.</summary>
        Stopped,
        /// <summary>On, waiting for the engine to start.</summary>
        Waiting,
        /// <summary>Its process runs.</summary>
        Running,
        /// <summary>It exited while it should run, and starts again after
        /// a pause.</summary>
        Restarting,
        /// <summary>Its file is not the one the user approved, so it was not
        /// started.</summary>
        Changed,
        /// <summary>It failed five times in a row and stays stopped until
        /// the user starts it.</summary>
        Failed,
        /// <summary>It could not be started, for <see cref="PluginRun.Refusal"/>.</summary>
        Refused,
        /// <summary>Its file is being opened and hashed before the start,
        /// off the interface's thread and outside the manager's lock.</summary>
        Checking,
    }

    /// <summary>What a line of a plugin's log says.</summary>
    internal enum PluginLogKind
    {
        /// <summary>A line the plugin wrote with <c>log</c>.</summary>
        Wrote,
        Started,
        /// <summary>It exited, with <see cref="PluginLogLine.Code"/>.</summary>
        Exited,
        /// <summary>It was not started, for <see cref="PluginLogLine.Refusal"/>.</summary>
        NotStarted,
        /// <summary>Its file changed since it was approved.</summary>
        Changed,
        /// <summary>It stopped after five failures in a row.</summary>
        Failed,
        /// <summary>A program PadForge did not start offered its key.</summary>
        ForeignKey,
    }

    /// <summary>One line of a plugin's log, kept as facts so the page words
    /// it in the language in use.</summary>
    internal readonly record struct PluginLogLine(DateTimeOffset Time, PluginLogKind Kind, string Text = null, int Code = 0,
        PluginRefusal Refusal = PluginRefusal.None);

    /// <summary>One plugin the user added, as it stands now. Read its
    /// members under <see cref="PluginManager"/>'s snapshot: the manager
    /// changes them under its lock.</summary>
    internal sealed class PluginRun
    {
        internal PluginRun(PluginData saved)
        {
            Path = saved.Path ?? string.Empty;
            Identity = saved.Identity ?? string.Empty;
            Name = ProgramDeviceDeclaration.Clean(saved.Name, ProgramDeviceDeclaration.LongestName);
            Sha256 = saved.Sha256 ?? string.Empty;
            Enabled = saved.Enabled;
        }

        /// <summary>The file, as the user picked it.</summary>
        public string Path { get; }

        /// <summary>The name its devices' identity is made from, fixed when
        /// it was added, so its mappings hold whatever name it gives.</summary>
        public string Identity { get; }

        /// <summary>The name its hello gave, or empty until it has given
        /// one.</summary>
        public string Name { get; internal set; }

        /// <summary>The SHA-256 the user approved, as hex.</summary>
        public string Sha256 { get; internal set; }

        public bool Enabled { get; internal set; }

        public PluginStatus Status { get; internal set; }
        public PluginRefusal Refusal { get; internal set; }

        /// <summary>The Windows error of a refused start.</summary>
        public int Error { get; internal set; }

        /// <summary>Its failures in a row.</summary>
        public int Failures { get; internal set; }

        /// <summary>The pause before the next start, while
        /// <see cref="PluginStatus.Restarting"/>.</summary>
        public int PauseMs { get; internal set; }

        /// <summary>The last exit code, or null before it first
        /// exited.</summary>
        public int? ExitCode { get; internal set; }

        /// <summary>The name shown: the name it gave, or its file's
        /// name until it has run and given one.</summary>
        public string ShownName => Name.Length > 0 ? Name : DisplayText.StripFormatCharacters(System.IO.Path.GetFileName(Path));

        // ── The run in progress, under the manager's lock ──
        internal IPluginProcess Process;
        internal FileStream Held;
        internal string Token;
        internal bool TokenTaken;
        internal bool Stopping;
        internal long StartedAtMs;
        internal long DueAtMs;
        /// <summary>A start whose file is being opened and hashed, and the
        /// number of that start: a stop in the meantime moves the number on,
        /// so the check that comes back late starts nothing.</summary>
        internal bool CheckPending;
        internal int CheckNumber;
        internal AutomationPluginGrant Grant;
        internal readonly Queue<PluginLogLine> Log = new();

        internal PluginData Save() => new()
        {
            Path = Path,
            Identity = Identity,
            Name = Name.Length > 0 ? Name : null,
            Sha256 = Sha256,
            Enabled = Enabled,
        };
    }

    /// <summary>
    /// Runs the plugins the user added and approved (#597). A plugin is a
    /// program PadForge starts with the user's ordinary rights through
    /// <see cref="IPluginLauncher"/>, never inside PadForge and never with
    /// its rights. It connects to <c>PadForge.Automation</c> with the
    /// one-time key it finds in its environment, and from then on its
    /// devices are like any program's.
    ///
    /// <para>Approval is for the exact bytes. Before each start the file is
    /// opened so nothing can change it, hashed again, and held open while
    /// the plugin runs: the handle that was hashed is the one held, and it
    /// shares reading only. The open and the hash run off the interface's
    /// thread and outside the lock, so a large file holds neither the page
    /// nor the other plugins. A file whose hash is not the approved one is
    /// not started.</para>
    ///
    /// <para>Plugins whose switch is on run while Plugins is on and the
    /// engine runs. One that exits while it should run starts again after
    /// 1 s, 2 s, 4 s and so on up to 30 s. Five failures in a row stop it,
    /// and a run that lasted a minute clears the count. Each keeps its last
    /// <see cref="LogLines"/> log lines.</para>
    ///
    /// <para>The process watching follows <c>GameModSender</c>: the handle
    /// is held, so a reused process ID is never taken for the plugin. Every
    /// member can be called from any thread. <see cref="Changed"/> is raised
    /// outside the lock, on the thread that made the change.</para>
    /// </summary>
    internal sealed class PluginManager : IDisposable
    {
        internal const int MostFailures = 5;
        internal const int FirstPauseMs = 1000;
        internal const int LongestPauseMs = 30000;
        internal const int SteadyRunMs = 60000;
        internal const int LogLines = 200;
        internal const int MostPlugins = 16;

        /// <summary>The variables a plugin reads to connect.</summary>
        internal const string TokenVariable = "PADFORGE_PLUGIN_TOKEN";
        internal const string PipeVariable = "PADFORGE_PIPE";

        private readonly object _sync = new();
        private readonly List<PluginRun> _runs = new();
        private readonly IPluginLauncher _launcher;
        private readonly string _pipeName;
        private readonly Func<long> _nowMs;
        private readonly Func<DateTimeOffset> _clock;
        private readonly Func<string, PluginCommand> _commandFor;
        private readonly Action<Action> _runCheck;
        private readonly Func<string, (FileStream Held, string Sha256)> _openHeld;
        private readonly List<(PluginRun Run, int Number)> _checks = new();
        private readonly Timer _timer;
        private bool _pluginsOn, _engine;
        private int _disposed;

        /// <param name="launcher">Starts the processes.</param>
        /// <param name="pipeName">The Automation pipe's name, which each
        /// plugin is given.</param>
        /// <param name="nowMs">The clock the pauses run on.</param>
        /// <param name="clock">The time the log is stamped with.</param>
        /// <param name="commandFor">How a file runs. A test gives its
        /// own.</param>
        /// <param name="timer">False for a test, which calls
        /// <see cref="Tick"/> itself.</param>
        /// <param name="runCheck">Where a start's open and hash run: the
        /// thread pool. A test can run them on its own thread, which is still
        /// after the lock is let go.</param>
        /// <param name="openHeld">Opens and hashes a file
        /// (<see cref="PluginFile.OpenHeld"/>). A test gives its own.</param>
        public PluginManager(IPluginLauncher launcher, string pipeName = null, Func<long> nowMs = null,
            Func<DateTimeOffset> clock = null, Func<string, PluginCommand> commandFor = null, bool timer = true,
            Action<Action> runCheck = null, Func<string, (FileStream Held, string Sha256)> openHeld = null)
        {
            _runCheck = runCheck ?? (work => ThreadPool.QueueUserWorkItem(_ => work()));
            _openHeld = openHeld ?? (path =>
            {
                var held = PluginFile.OpenHeld(path, out string sha);
                return (held, sha);
            });
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            _pipeName = pipeName ?? AutomationPipe.PipeName;
            _nowMs = nowMs ?? (() => Environment.TickCount64);
            _clock = clock ?? (() => DateTimeOffset.Now);
            _commandFor = commandFor ?? (path => PluginFile.CommandFor(path));
            if (timer) _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Raised when a plugin's state, its log or the list
        /// changed.</summary>
        public event Action Changed;

        /// <summary>Raised when what PadForge.xml holds of the plugins
        /// changed: a plugin added, removed, approved, switched or
        /// named.</summary>
        public event Action SavedChanged;

        /// <summary>The plugins, in the order they were added.</summary>
        public List<PluginRun> Snapshot()
        {
            lock (_sync) return new List<PluginRun>(_runs);
        }

        /// <summary>A copy of a plugin's log, oldest line first.</summary>
        public List<PluginLogLine> LogOf(PluginRun run)
        {
            lock (_sync) return run == null ? new List<PluginLogLine>() : new List<PluginLogLine>(run.Log);
        }

        /// <summary>How many plugins run now.</summary>
        public int RunningCount
        {
            get
            {
                lock (_sync)
                {
                    int n = 0;
                    foreach (var run in _runs) if (run.Status == PluginStatus.Running) n++;
                    return n;
                }
            }
        }

        /// <summary>Whether <paramref name="grant"/> is the one PadForge
        /// gave a plugin's present run.</summary>
        public bool IsPluginsGrant(AutomationPluginGrant grant)
        {
            if (grant == null) return false;
            lock (_sync)
            {
                foreach (var run in _runs) if (ReferenceEquals(run.Grant, grant)) return true;
                return false;
            }
        }

        // ── The list ──

        /// <summary>Takes the plugins PadForge.xml holds, in place of the
        /// list there was, and starts those that should run.</summary>
        public void Load(IEnumerable<PluginData> saved)
        {
            lock (_sync)
            {
                // A list loaded again, as a factory reset does, ends the
                // plugins of the old one first.
                foreach (var old in _runs)
                {
                    old.Enabled = false;
                    StopRun(old);
                }
                _runs.Clear();
                if (saved != null)
                    foreach (var data in saved)
                    {
                        if (data == null || string.IsNullOrWhiteSpace(data.Path) || string.IsNullOrWhiteSpace(data.Sha256)) continue;
                        if (_runs.Count >= MostPlugins) break;
                        if (string.IsNullOrWhiteSpace(data.Identity)) data.Identity = UniqueIdentity(data.Path);
                        _runs.Add(new PluginRun(data));
                    }
                foreach (var run in _runs) Evaluate(run);
            }
            RunChecks();
            Changed?.Invoke();
        }

        /// <summary>The plugins as PadForge.xml holds them.</summary>
        public PluginData[] Save()
        {
            lock (_sync)
            {
                var all = new PluginData[_runs.Count];
                for (int i = 0; i < all.Length; i++) all[i] = _runs[i].Save();
                return all;
            }
        }

        /// <summary>Whether a plugin of this file is on the list.</summary>
        public bool Has(string path)
        {
            string full = Full(path);
            lock (_sync)
                foreach (var run in _runs)
                    if (string.Equals(Full(run.Path), full, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Adds a plugin the user approved in PadForge's own confirmation,
        /// for the bytes whose SHA-256 the confirmation showed, switched on.
        /// Null when the list is full or holds the file already.
        /// </summary>
        public PluginRun Add(string path, string sha256)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sha256)) return null;
            PluginRun run;
            lock (_sync)
            {
                if (_runs.Count >= MostPlugins || Has(path)) return null;
                run = new PluginRun(new PluginData
                {
                    Path = Full(path),
                    Identity = UniqueIdentity(path),
                    Sha256 = sha256.ToUpperInvariant(),
                    Enabled = true,
                });
                _runs.Add(run);
                Evaluate(run);
            }
            RunChecks();
            SavedChanged?.Invoke();
            Changed?.Invoke();
            return run;
        }

        /// <summary>Stops a plugin and takes it off the list. Its devices'
        /// rows stay on the Devices page as disconnected devices.</summary>
        public void Remove(PluginRun run)
        {
            lock (_sync)
            {
                if (!_runs.Remove(run)) return;
                run.Enabled = false;
                StopRun(run);
            }
            SavedChanged?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>The plugin's switch. On clears its failures and starts
        /// it when it should run.</summary>
        public void SetEnabled(PluginRun run, bool on)
        {
            lock (_sync)
            {
                if (!_runs.Contains(run) || run.Enabled == on) return;
                run.Enabled = on;
                run.Failures = 0;
                if (on)
                {
                    if (run.Status is PluginStatus.Failed or PluginStatus.Refused) run.Status = PluginStatus.Stopped;
                    Evaluate(run);
                }
                else StopRun(run);
            }
            RunChecks();
            SavedChanged?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>The user approved a changed file again, for the bytes
        /// whose SHA-256 the confirmation showed.</summary>
        public void Approve(PluginRun run, string sha256)
        {
            if (string.IsNullOrWhiteSpace(sha256)) return;
            lock (_sync)
            {
                if (!_runs.Contains(run)) return;
                run.Sha256 = sha256.ToUpperInvariant();
                run.Failures = 0;
                if (run.Status == PluginStatus.Changed) run.Status = PluginStatus.Stopped;
                Evaluate(run);
            }
            RunChecks();
            SavedChanged?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>Start, after five failures or a start Windows refused:
        /// the count is cleared and the plugin starts when it should
        /// run.</summary>
        public void StartAgain(PluginRun run)
        {
            lock (_sync)
            {
                if (!_runs.Contains(run)) return;
                run.Failures = 0;
                if (run.Status is PluginStatus.Failed or PluginStatus.Refused or PluginStatus.Changed) run.Status = PluginStatus.Stopped;
                Evaluate(run);
            }
            RunChecks();
            Changed?.Invoke();
        }

        // ── When plugins run ──

        /// <summary>The Plugins switch.</summary>
        public void SetPluginsOn(bool on)
        {
            lock (_sync)
            {
                if (_pluginsOn == on) return;
                _pluginsOn = on;
                EvaluateAll(fresh: on);
            }
            RunChecks();
            Changed?.Invoke();
        }

        /// <summary>The engine started or stopped.</summary>
        public void SetEngine(bool running)
        {
            lock (_sync)
            {
                if (_engine == running) return;
                _engine = running;
                EvaluateAll(fresh: running);
            }
            RunChecks();
            Changed?.Invoke();
        }

        /// <summary>Under the lock. <paramref name="fresh"/>: a new chance
        /// for every plugin, so one that failed five times last time is
        /// tried again with the engine's next start.</summary>
        private void EvaluateAll(bool fresh)
        {
            foreach (var run in _runs)
            {
                if (fresh && run.Status is PluginStatus.Failed or PluginStatus.Refused or PluginStatus.Changed)
                {
                    run.Failures = 0;
                    run.Status = PluginStatus.Stopped;
                }
                Evaluate(run);
            }
        }

        private bool Wanted(PluginRun run) => _pluginsOn && _engine && run.Enabled && Volatile.Read(ref _disposed) == 0;

        /// <summary>Under the lock: starts the plugin when it should run and
        /// does not, and stops it when it runs and should not.</summary>
        private void Evaluate(PluginRun run)
        {
            if (!Wanted(run))
            {
                StopRun(run);
                return;
            }
            if (run.Process != null || run.CheckPending || run.Status is PluginStatus.Failed or PluginStatus.Restarting) return;
            if (run.Status is PluginStatus.Changed or PluginStatus.Refused) return;
            Start(run);
        }

        /// <summary>Under the lock: ends the run, if one goes on, and says
        /// where the plugin stands.</summary>
        private void StopRun(PluginRun run)
        {
            // Switched off, or Plugins is: stopped. Waiting for the engine:
            // a plugin held back for a reason keeps saying it.
            if (!run.Enabled || !_pluginsOn) run.Status = PluginStatus.Stopped;
            else if (run.Status is not (PluginStatus.Changed or PluginStatus.Failed or PluginStatus.Refused))
                run.Status = PluginStatus.Waiting;
            run.DueAtMs = 0;
            if (run.CheckPending)
            {
                // The check still out finds its number gone and starts
                // nothing. It lets go of the file itself.
                run.CheckPending = false;
                run.CheckNumber++;
            }
            if (run.Process == null) return;
            run.Stopping = true;
            run.Token = null;
            try { run.Process.Stop(); } catch { }
        }

        /// <summary>Under the lock: a start, whose file is opened and hashed
        /// once the lock is let go (<see cref="RunChecks"/>).</summary>
        private void Start(PluginRun run)
        {
            run.CheckPending = true;
            run.CheckNumber++;
            run.Status = PluginStatus.Checking;
            _checks.Add((run, run.CheckNumber));
        }

        /// <summary>Outside the lock: hands every start the lock held back to
        /// the check's runner.</summary>
        private void RunChecks()
        {
            List<(PluginRun Run, int Number)> due;
            lock (_sync)
            {
                if (_checks.Count == 0) return;
                due = new List<(PluginRun, int)>(_checks);
                _checks.Clear();
            }
            foreach (var (run, number) in due) _runCheck(() => Check(run, number));
        }

        /// <summary>Opens and hashes the file of one start with no lock held,
        /// then, under the lock, starts the plugin when that start still
        /// stands. The handle that was hashed is the one the run holds.</summary>
        private void Check(PluginRun run, int number)
        {
            FileStream held;
            string sha;
            try { (held, sha) = _openHeld(run.Path); }
            catch { (held, sha) = (null, null); }
            bool changed = true;
            lock (_sync)
            {
                if (!run.CheckPending || run.CheckNumber != number)
                {
                    // Stopped, switched off or started again while the file
                    // was read.
                    held?.Dispose();
                    changed = false;
                }
                else
                {
                    run.CheckPending = false;
                    if (!_runs.Contains(run) || !Wanted(run))
                    {
                        held?.Dispose();
                        if (run.Status == PluginStatus.Checking) run.Status = PluginStatus.Stopped;
                        if (_runs.Contains(run)) Evaluate(run);
                    }
                    else Launch(run, held, sha);
                }
            }
            RunChecks();
            if (changed) Changed?.Invoke();
        }

        /// <summary>Under the lock: the rest of a start, from the hash just
        /// read to the running process.</summary>
        private void Launch(PluginRun run, FileStream held, string sha)
        {
            if (held == null)
            {
                Refuse(run, PluginRefusal.Missing, 0, retry: false);
                return;
            }
            if (!string.Equals(sha, run.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                held.Dispose();
                run.Status = PluginStatus.Changed;
                AddLog(run, new PluginLogLine(_clock(), PluginLogKind.Changed));
                return;
            }
            PluginCommand command;
            try { command = _commandFor(run.Path); }
            catch { command = null; }
            if (command == null)
            {
                held.Dispose();
                Refuse(run, PluginRefusal.NoProgram, 0, retry: false);
                return;
            }

            string token = NewToken();
            PluginLaunch launch;
            try
            {
                launch = _launcher.Start(command, new Dictionary<string, string>
                {
                    [TokenVariable] = token,
                    [PipeVariable] = _pipeName,
                });
            }
            catch
            {
                launch = PluginLaunch.Refused(PluginRefusal.Failed);
            }
            if (launch?.Process == null)
            {
                held.Dispose();
                var why = launch?.Refusal ?? PluginRefusal.Failed;
                // No shell yet, as at sign-in, or a start Windows refused
                // this once: tried again after the pause. The rest stays
                // as it is until the user acts.
                Refuse(run, why == PluginRefusal.None ? PluginRefusal.Failed : why, launch?.Error ?? 0,
                    retry: why is PluginRefusal.NoShell or PluginRefusal.Failed or PluginRefusal.None);
                return;
            }

            run.Process = launch.Process;
            run.Held = held;
            run.Token = token;
            run.TokenTaken = false;
            run.Grant = null;
            run.Stopping = false;
            run.StartedAtMs = _nowMs();
            run.Status = PluginStatus.Running;
            run.Refusal = PluginRefusal.None;
            run.Error = 0;
            AddLog(run, new PluginLogLine(_clock(), PluginLogKind.Started));
            var process = launch.Process;
            process.Exited += code => OnExited(run, process, code);
        }

        /// <summary>Under the lock.</summary>
        private void Refuse(PluginRun run, PluginRefusal why, int error, bool retry)
        {
            run.Refusal = why;
            run.Error = error;
            AddLog(run, new PluginLogLine(_clock(), PluginLogKind.NotStarted, Code: error, Refusal: why));
            if (retry) Failed(run);
            else run.Status = PluginStatus.Refused;
        }

        /// <summary>Under the lock: one more failure in a row. The fifth
        /// stops the plugin. Before it, the next start waits 1 s, then 2 s,
        /// 4 s and so on, never more than 30 s.</summary>
        private void Failed(PluginRun run)
        {
            run.Failures++;
            if (run.Failures >= MostFailures)
            {
                run.Status = PluginStatus.Failed;
                run.DueAtMs = 0;
                AddLog(run, new PluginLogLine(_clock(), PluginLogKind.Failed));
                return;
            }
            run.PauseMs = PauseAfter(run.Failures);
            run.DueAtMs = _nowMs() + run.PauseMs;
            run.Status = PluginStatus.Restarting;
            EnsureTimer();
        }

        /// <summary>The pause before the start that follows
        /// <paramref name="failures"/> failures in a row.</summary>
        internal static int PauseAfter(int failures)
        {
            if (failures <= 1) return FirstPauseMs;
            long pause = (long)FirstPauseMs << Math.Min(failures - 1, 16);
            return (int)Math.Min(pause, LongestPauseMs);
        }

        private void OnExited(PluginRun run, IPluginProcess process, int code)
        {
            bool changed;
            lock (_sync)
            {
                changed = run.Process == process;
                if (changed)
                {
                    run.Process = null;
                    run.Held?.Dispose();
                    run.Held = null;
                    run.Token = null;
                    run.Grant = null;
                    run.ExitCode = code;
                    AddLog(run, new PluginLogLine(_clock(), PluginLogKind.Exited, Code: code));
                    bool stopped = run.Stopping;
                    run.Stopping = false;
                    if (!stopped && _runs.Contains(run) && Wanted(run))
                    {
                        // A run that lasted a minute was a good one: the
                        // failures before it no longer count.
                        if (_nowMs() - run.StartedAtMs >= SteadyRunMs) run.Failures = 0;
                        Failed(run);
                    }
                    else if (_runs.Contains(run))
                    {
                        // Stopped by PadForge: it may be wanted again by now.
                        if (run.Status == PluginStatus.Running) run.Status = PluginStatus.Stopped;
                        Evaluate(run);
                    }
                }
            }
            // The handle is let go off the process's own wait.
            ThreadPool.QueueUserWorkItem(_ => process.Dispose());
            RunChecks();
            if (changed) Changed?.Invoke();
        }

        /// <summary>Starts the plugins whose pause ran out. The manager's
        /// timer calls it, and a test calls it after moving its
        /// clock.</summary>
        internal void Tick()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            bool changed = false, waiting = false;
            lock (_sync)
            {
                long now = _nowMs();
                foreach (var run in _runs)
                {
                    if (run.Status != PluginStatus.Restarting) continue;
                    if (now < run.DueAtMs) { waiting = true; continue; }
                    run.DueAtMs = 0;
                    run.Status = PluginStatus.Stopped;
                    changed = true;
                    if (Wanted(run)) Start(run);
                    else StopRun(run);
                    if (run.Status == PluginStatus.Restarting) waiting = true;
                }
                if (!waiting) StopTimer();
            }
            RunChecks();
            if (changed) Changed?.Invoke();
        }

        private void EnsureTimer()
        {
            try { _timer?.Change(250, 250); } catch (ObjectDisposedException) { }
        }

        private void StopTimer()
        {
            try { _timer?.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }

        // ── The key ──

        /// <summary>
        /// The host's <see cref="AutomationHost.AcceptPluginToken"/>: takes a
        /// plugin's key once, only while that plugin runs and has not used
        /// it, and only from the process PadForge started for it or one in
        /// its job. Gives the approved plugin's grant, or null.
        /// </summary>
        public AutomationPluginGrant AcceptToken(string token, int processId)
        {
            if (string.IsNullOrEmpty(token)) return null;
            byte[] offered = Encoding.UTF8.GetBytes(token);
            PluginRun foreign = null;
            AutomationPluginGrant grant = null;
            lock (_sync)
            {
                foreach (var run in _runs)
                {
                    if (run.Token == null || run.Process == null || run.Status != PluginStatus.Running) continue;
                    if (!CryptographicOperations.FixedTimeEquals(offered, Encoding.UTF8.GetBytes(run.Token))) continue;
                    if (run.TokenTaken) return null;
                    if (!run.Process.Owns(processId))
                    {
                        foreign = run;
                        break;
                    }
                    run.TokenTaken = true;
                    var mine = run;
                    grant = new AutomationPluginGrant(run.Identity, line => Wrote(mine, line), name => Named(mine, name));
                    run.Grant = grant;
                    break;
                }
                if (foreign != null) AddLog(foreign, new PluginLogLine(_clock(), PluginLogKind.ForeignKey, Code: processId));
            }
            if (foreign != null) Changed?.Invoke();
            return grant;
        }

        private void Wrote(PluginRun run, string line)
        {
            lock (_sync)
            {
                if (!_runs.Contains(run)) return;
                AddLog(run, new PluginLogLine(_clock(), PluginLogKind.Wrote, ProgramDeviceDeclaration.Clean(line, AutomationSessions.LongestLogLine)));
            }
            Changed?.Invoke();
        }

        private void Named(PluginRun run, string name)
        {
            bool saved;
            name = ProgramDeviceDeclaration.Clean(name, ProgramDeviceDeclaration.LongestName);
            lock (_sync)
            {
                saved = _runs.Contains(run) && name.Length > 0 && run.Name != name;
                if (saved) run.Name = name;
            }
            if (!saved) return;
            SavedChanged?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>Under the lock. The log keeps its last
        /// <see cref="LogLines"/> lines.</summary>
        private static void AddLog(PluginRun run, PluginLogLine line)
        {
            run.Log.Enqueue(line);
            while (run.Log.Count > LogLines) run.Log.Dequeue();
        }

        private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        private static string Full(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path ?? string.Empty; }
        }

        /// <summary>Under the lock, or before any other thread sees the list:
        /// the file's name, and a number after it when another plugin on
        /// the list has that name already.</summary>
        private string UniqueIdentity(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path ?? string.Empty);
            if (stem.Length == 0) stem = "Plugin";
            string name = stem;
            for (int n = 2; Taken(name); n++) name = stem + " " + n;
            return name;
        }

        private bool Taken(string identity)
        {
            foreach (var run in _runs)
                if (string.Equals(run.Identity, identity, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_sync)
            {
                foreach (var run in _runs) StopRun(run);
            }
            _timer?.Dispose();
        }
    }
}
