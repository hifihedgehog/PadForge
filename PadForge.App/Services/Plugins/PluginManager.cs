using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PadForge.Services.Automation;
using PadForge.Engine.Common;

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
        /// <summary>It exited, or could not be started, while it should
        /// run, and starts again after a pause.</summary>
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
        /// <summary>It sent nothing for a minute and was ended (#600 item
        /// 18).</summary>
        Silent,
        /// <summary>Its file changed while it ran (#600 item 18).</summary>
        ChangedWhileRunning,
        /// <summary>A new version signed with the key the user trusts was
        /// taken without asking.</summary>
        TrustedVersion,
        /// <summary>It had not said hello by its deadline and was
        /// ended.</summary>
        NeverConnected,
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
            Signer = CleanSigner(saved.Signer);
            SignerKey = string.IsNullOrWhiteSpace(saved.SignerKey) ? null : saved.SignerKey.ToUpperInvariant();
            TrustSigner = saved.TrustSigner && SignerKey != null;
            Settings.Load(saved.Values);
        }

        internal static string CleanSigner(string signer)
            => string.IsNullOrWhiteSpace(signer) ? null : ProgramDeviceDeclaration.Clean(signer, 128);

        /// <summary>What it declared and the values the user set for it
        /// (#600 item 17).</summary>
        public PluginSettingSet Settings { get; } = new();

        /// <summary>The signer of the approved bytes, as the confirmation
        /// showed it, or null for a file with none (#600 item 18).</summary>
        public string Signer { get; internal set; }

        /// <summary>The key that signed the approved bytes, or null for bytes
        /// whose signature did not verify with revocation checked.</summary>
        public string SignerKey { get; internal set; }

        /// <summary>Trust New Versions From This Signer: a changed file whose
        /// signature verifies with revocation checked and whose key is
        /// <see cref="SignerKey"/> starts without Approve and Restart. Off by
        /// default, and only while a key is kept.</summary>
        public bool TrustSigner { get; internal set; }

        /// <summary>Its file changed while it ran: the row offers Approve and
        /// Restart (#600 item 18).</summary>
        public bool FileChanged { get; internal set; }

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

        /// <summary>Its last run started and never said hello by its
        /// deadline (<see cref="PluginManager.HelloDeadlineMs"/>). Its card
        /// and Home's Plugins tile say so until a run connects or PadForge
        /// stops it.</summary>
        public bool NeverConnected { get; internal set; }

        /// <summary>The last exit code, or null before it first
        /// exited.</summary>
        public int? ExitCode { get; internal set; }

        /// <summary>The name shown: the name it gave, or its file's
        /// name until it has run and given one.</summary>
        public string ShownName => Name.Length > 0 ? Name : (ShownText.Clean(System.IO.Path.GetFileName(Path)) ?? string.Empty);

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
        /// <summary>The file's size and write time when it was let go at the
        /// plugin's hello, which a later look compares.</summary>
        internal (long Length, DateTime Written)? Stamp;
        internal bool HashPending;

        internal PluginData Save() => new()
        {
            Path = Path,
            Identity = Identity,
            Name = Name.Length > 0 ? Name : null,
            Sha256 = Sha256,
            Enabled = Enabled,
            Signer = Signer,
            SignerKey = SignerKey,
            TrustSigner = TrustSigner,
            Values = Settings.Save(),
        };
    }

    /// <summary>
    /// Runs the plugins the user added and approved (#600). A plugin is a
    /// program PadForge starts with the user's ordinary rights through
    /// <see cref="IPluginLauncher"/>, never inside PadForge and never with
    /// its rights. It connects to <c>PadForge.Automation</c> with the
    /// one-time key it finds in its environment, and from then on its
    /// devices are like any program's.
    ///
    /// <para>Approval is for the exact bytes. Before each start the file is
    /// opened so nothing can change it, hashed again, and held open until
    /// the plugin's hello: the handle that was hashed is the one held, and
    /// it shares reading only. After the hello the file is free to change,
    /// and a change shows Approve and Restart
    /// (<see cref="CheckFiles"/>). The open and the hash run off the interface's
    /// thread and outside the lock, so a large file holds neither the page
    /// nor the other plugins. A file whose hash is not the approved one is
    /// not started.</para>
    ///
    /// <para>Plugins whose switch is on run while Plugins is on and the
    /// engine runs. One that exits while it should run starts again after
    /// 1 s, then 2 s, 4 s and 8 s. The fifth failure in a row stops it,
    /// and a run that said hello and lasted a minute clears the count. Each
    /// keeps its last
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
        internal const int SteadyRunMs = 60000;
        internal const int LogLines = 200;
        internal const int MostPlugins = 16;

        /// <summary>How long a plugin has from its start to say hello. After
        /// its hello, a plugin that says nothing for a minute is ended
        /// (<see cref="PluginsController.EndAfterSilenceMs"/>). One that has
        /// said nothing since its start is held to the same minute, which
        /// also leaves a cold start of an interpreter or a file being
        /// scanned before it runs its time. The shorter silence rule, its
        /// devices offline at five seconds, has nothing to act on before a
        /// hello, since a plugin adds devices only once connected.</summary>
        internal const int HelloDeadlineMs = PluginsController.EndAfterSilenceMs;

        /// <summary>The variables a plugin reads to connect.</summary>
        internal const string TokenVariable = "PADFORGE_PLUGIN_TOKEN";
        internal const string PipeVariable = "PADFORGE_PIPE";

        private readonly object _sync = new();
        private readonly List<PluginRun> _runs = new();
        private readonly IPluginLauncher _launcher;
        private readonly string _pipeName;
        private readonly Func<long> _nowMs;
        private readonly Func<DateTimeOffset> _clock;
        /// <summary>How a file runs: the approved path, whose extension
        /// picks the kind of start, and the path its held handle reached,
        /// which the command runs.</summary>
        private readonly Func<string, string, PluginCommand> _commandFor;
        private readonly Action<Action> _runCheck;
        private readonly Func<string, (FileStream Held, string Sha256)> _openHeld;
        private readonly Func<string, FileStream, AuthenticodeSignature> _signatureOf;
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
        /// <param name="signatureOf">Verifies the signature of a held file
        /// with revocation checked (<see cref="PluginFile.SignatureOf"/>). A
        /// test gives its own, since the real one asks the certificate
        /// authorities' servers.</param>
        public PluginManager(IPluginLauncher launcher, string pipeName = null, Func<long> nowMs = null,
            Func<DateTimeOffset> clock = null, Func<string, PluginCommand> commandFor = null, bool timer = true,
            Action<Action> runCheck = null, Func<string, (FileStream Held, string Sha256)> openHeld = null,
            Func<string, FileStream, AuthenticodeSignature> signatureOf = null)
        {
            _signatureOf = signatureOf ?? PluginFile.SignatureOf;
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
            _commandFor = commandFor != null
                ? (_, final) => commandFor(final)
                : (approved, final) => PluginFile.CommandFor(final, kindOf: approved);
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

        /// <summary>The plugin a grant was given to, or null.</summary>
        public PluginRun RunOf(AutomationPluginGrant grant)
        {
            if (grant == null) return null;
            lock (_sync)
            {
                foreach (var run in _runs) if (ReferenceEquals(run.Grant, grant)) return run;
                return null;
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
        /// for the bytes whose SHA-256 the confirmation showed, switched on,
        /// with the key that signed them when their signature verified.
        /// Null when the list is full or holds the file already.
        /// </summary>
        public PluginRun Add(string path, string sha256, string signer = null, string signerKey = null)
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
                    Signer = signer,
                    SignerKey = signerKey,
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
        /// whose SHA-256 the confirmation showed. Bytes signed with another
        /// key, or with none, turn Trust New Versions From This Signer off,
        /// since it names the signer of the bytes the user trusted.</summary>
        public void Approve(PluginRun run, string sha256, string signer = null, string signerKey = null)
        {
            if (string.IsNullOrWhiteSpace(sha256)) return;
            lock (_sync)
            {
                if (!_runs.Contains(run)) return;
                run.Sha256 = sha256.ToUpperInvariant();
                run.Signer = PluginRun.CleanSigner(signer);
                signerKey = string.IsNullOrWhiteSpace(signerKey) ? null : signerKey.ToUpperInvariant();
                if (signerKey == null || signerKey != run.SignerKey) run.TrustSigner = false;
                run.SignerKey = signerKey;
                run.Failures = 0;
                if (run.Status == PluginStatus.Changed) run.Status = PluginStatus.Stopped;
                if (run.FileChanged && run.Process != null)
                {
                    // Approve and Restart: the run of the old bytes ends, and
                    // its end starts the new ones (OnExited).
                    run.FileChanged = false;
                    StopRun(run);
                }
                else
                {
                    run.FileChanged = false;
                    Evaluate(run);
                }
            }
            RunChecks();
            SavedChanged?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>Trust New Versions From This Signer. It turns on only
        /// while the approved bytes' key is kept.</summary>
        public void SetTrust(PluginRun run, bool on)
        {
            lock (_sync)
            {
                if (!_runs.Contains(run) || run.TrustSigner == on || (on && run.SignerKey == null)) return;
                run.TrustSigner = on;
            }
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
            // a plugin held back for a reason keeps saying it. With the
            // engine running it is stopped to start again (Approve and
            // Restart, a trusted new version), and keeps its state until
            // its exit starts it (OnExited).
            if (!run.Enabled || !_pluginsOn) run.Status = PluginStatus.Stopped;
            else if (!_engine && run.Status is not (PluginStatus.Changed or PluginStatus.Failed or PluginStatus.Refused))
                run.Status = PluginStatus.Waiting;
            run.DueAtMs = 0;
            run.NeverConnected = false;
            if (run.CheckPending)
            {
                // The check still out finds its number gone and starts
                // nothing. It lets go of the file itself.
                run.CheckPending = false;
                run.CheckNumber++;
            }
            if (run.Process == null) return;
            if (run.Grant != null) run.Grant.Stopped = true;
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
            var trusted = Trusted(run, held, sha);
            string key = KeyToKeep(run, held, sha);
            string final = null;
            int finalError = 0;
            try { final = PluginFile.FinalPath(held, out finalError); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { finalError = ex.HResult; }
            // How the file runs is read here, with no lock held: the read of
            // the desktop user's registry holds no page.
            PluginCommand command = null;
            if (final != null)
            {
                try { command = _commandFor(run.Path, final); }
                catch { command = null; }
            }
            bool changed = true, adopted = false;
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
                    else adopted = Launch(run, held, sha, trusted, key, final, finalError, command);
                }
            }
            RunChecks();
            if (adopted) SavedChanged?.Invoke();
            if (changed) Changed?.Invoke();
        }

        /// <summary>
        /// Outside the lock: the signature of a file whose hash is not the
        /// approved one, verified with revocation checked on the handle that
        /// was hashed, while the user trusts new versions from the approved
        /// bytes' signer. Null for anything else, which asks the user as
        /// before. Whether its key is the trusted one is settled under the
        /// lock, against the key the run holds then
        /// (<see cref="TakesTrusted"/>).
        /// </summary>
        private AuthenticodeSignature Trusted(PluginRun run, FileStream held, string sha)
        {
            string approved;
            lock (_sync)
            {
                if (!run.TrustSigner || run.SignerKey == null) return null;
                approved = run.Sha256;
            }
            if (held == null || sha == null || string.Equals(sha, approved, StringComparison.OrdinalIgnoreCase)) return null;
            AuthenticodeSignature signature;
            try { signature = _signatureOf(run.Path, held); }
            catch { signature = null; }
            return signature?.Key != null ? signature : null;
        }

        /// <summary>
        /// Outside the lock: the key of a plugin approved before keys were
        /// kept, read on the handle that was hashed while the file is the
        /// approved bytes, so its trust switch shows with no new approval.
        /// Only an approval that saw a signer is read. Null for anything
        /// else. The run is checked again under the lock before it is kept
        /// (<see cref="Launch"/>).
        /// </summary>
        private string KeyToKeep(PluginRun run, FileStream held, string sha)
        {
            if (held == null || sha == null) return null;
            lock (_sync)
            {
                if (run.SignerKey != null || run.Signer == null || !string.Equals(sha, run.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
            }
            try { return PluginFile.KeyOf(run.Path, held); }
            catch { return null; }
        }

        /// <summary>Under the lock: whether the run still trusts the key the
        /// signature was checked against.</summary>
        private static bool TakesTrusted(PluginRun run, AuthenticodeSignature trusted)
            => trusted != null && run.TrustSigner && run.SignerKey != null
               && string.Equals(trusted.Key, run.SignerKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>Under the lock: a new version signed with the trusted key
        /// becomes the approved bytes, as Approve would make them.</summary>
        private void TakeTrusted(PluginRun run, string sha, AuthenticodeSignature trusted)
        {
            run.Sha256 = sha.ToUpperInvariant();
            run.Signer = PluginRun.CleanSigner(trusted.Signer);
            run.FileChanged = false;
            AddLog(run, new PluginLogLine(_clock(), PluginLogKind.TrustedVersion));
        }

        /// <summary>Under the lock: the rest of a start, from the hash just
        /// read to the running process. True when what PadForge.xml keeps of
        /// it changed: it took a new version signed with the trusted key, or
        /// kept the key of an approval from before keys were kept. The start
        /// is made from <paramref name="final"/>, the path the held handle
        /// reached (<see cref="PluginFile.FinalPath"/>), with the
        /// <paramref name="command"/> read for it.</summary>
        private bool Launch(PluginRun run, FileStream held, string sha, AuthenticodeSignature trusted, string key,
            string final, int finalError, PluginCommand command)
        {
            if (held == null)
            {
                Refuse(run, PluginRefusal.Missing, 0, retry: false);
                return false;
            }
            bool adopted = false;
            if (!string.Equals(sha, run.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                if (!TakesTrusted(run, trusted))
                {
                    held.Dispose();
                    run.Status = PluginStatus.Changed;
                    AddLog(run, new PluginLogLine(_clock(), PluginLogKind.Changed));
                    return false;
                }
                TakeTrusted(run, sha, trusted);
                adopted = true;
            }
            else if (key != null && run.SignerKey == null)
            {
                run.SignerKey = key.ToUpperInvariant();
                adopted = true;
            }
            if (final == null)
            {
                held.Dispose();
                Refuse(run, PluginRefusal.Failed, finalError, retry: false);
                return adopted;
            }
            if (command?.Program == null)
            {
                held.Dispose();
                Refuse(run, command?.Refusal is { } why and not PluginRefusal.None ? why : PluginRefusal.NoProgram, 0, retry: false);
                return adopted;
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
                // A start Windows refused this once is tried again after
                // the pause. The rest stays as it is until the user acts.
                Refuse(run, why == PluginRefusal.None ? PluginRefusal.Failed : why, launch?.Error ?? 0,
                    retry: why is PluginRefusal.Failed or PluginRefusal.None);
                return adopted;
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
            return adopted;
        }

        /// <summary>Under the lock: a start that did not happen. Windows'
        /// refusals count toward the five.</summary>
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
        /// 4 s and 8 s.</summary>
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
        /// <paramref name="failures"/> failures in a row, which
        /// <see cref="Failed"/> asks for one to four: 1, 2, 4 and 8 s.</summary>
        internal static int PauseAfter(int failures)
            => FirstPauseMs << (Math.Clamp(failures, 1, MostFailures - 1) - 1);

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
                    run.Stamp = null;
                    run.FileChanged = false;
                    run.Settings.EndRun();
                    run.ExitCode = code;
                    AddLog(run, new PluginLogLine(_clock(), PluginLogKind.Exited, Code: code));
                    bool stopped = run.Stopping;
                    run.Stopping = false;
                    if (!stopped && _runs.Contains(run) && Wanted(run))
                    {
                        // A run that said hello and lasted a minute was a
                        // good one: the failures before it no longer count.
                        // One that never said hello is no good however long
                        // it ran, so its pause grows (CheckHello).
                        if (run.TokenTaken && _nowMs() - run.StartedAtMs >= SteadyRunMs) run.Failures = 0;
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
            bool connected = false;
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
                    connected = run.NeverConnected;
                    run.NeverConnected = false;
                    var mine = run;
                    grant = new AutomationPluginGrant(run.Identity, line => Wrote(mine, line), name => Named(mine, name));
                    run.Grant = grant;
                    PluginLines.Bind(grant, run.Settings);
                    // Held from the hash until this hello, so nothing could
                    // change the file in between. From here it is free to be
                    // rebuilt, and a change shows Approve and Restart.
                    run.Stamp = StampOf(run.Held);
                    run.Held?.Dispose();
                    run.Held = null;
                    break;
                }
                if (foreign != null) AddLog(foreign, new PluginLogLine(_clock(), PluginLogKind.ForeignKey, Code: processId));
            }
            if (foreign != null || connected) Changed?.Invoke();
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

        // ── After the hello (#600 item 18) ──

        private static (long, DateTime)? StampOf(FileStream held)
        {
            if (held == null) return null;
            try { return (held.Length, File.GetLastWriteTimeUtc(held.Name)); }
            catch { return null; }
        }

        /// <summary>The size and write time of a file, or null. A test
        /// gives its own.</summary>
        internal Func<string, (long Length, DateTime Written)?> StatFile { get; set; } = path =>
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
            }
            catch { return null; }
        };

        /// <summary>
        /// Looks at the file of each plugin that runs past its hello, off
        /// the interface's thread, so a share that stops answering holds no
        /// window: when its size or write time moved, it is hashed again,
        /// and a hash that is not the approved one marks the run
        /// <see cref="PluginRun.FileChanged"/>. A new version signed with the
        /// key the user trusts restarts it instead, as Approve and Restart
        /// does, and its start hashes and checks the file again. A marked
        /// file is looked at again when it moves: one that is the approved
        /// bytes again, an edit taken back or a write read before it ended,
        /// is no longer marked, and a trusted version still being written
        /// when it was first read is taken once it is whole. One look per
        /// plugin is out at a time. The page's tick calls it once a second.
        /// </summary>
        public void CheckFiles()
        {
            var due = new List<PluginRun>();
            lock (_sync)
                foreach (var run in _runs)
                    if (run.Process != null && run.Held == null && run.Stamp != null && !run.HashPending)
                    {
                        run.HashPending = true;
                        due.Add(run);
                    }
            foreach (var run in due) _runCheck(() => LookAtFile(run));
        }

        /// <summary>Off the interface's thread: one plugin's look of
        /// <see cref="CheckFiles"/>.</summary>
        private void LookAtFile(PluginRun run)
        {
            var now = StatFile(run.Path);
            lock (_sync)
            {
                if (now == run.Stamp)
                {
                    run.HashPending = false;
                    return;
                }
            }
            string sha = null;
            FileStream held = null;
            AuthenticodeSignature trusted = null;
            try
            {
                (held, sha) = _openHeld(run.Path);
                trusted = Trusted(run, held, sha);
            }
            catch { sha = null; }
            finally { held?.Dispose(); }
            bool changed = false, adopted = false;
            lock (_sync)
            {
                run.HashPending = false;
                if (run.Process == null) return;
                run.Stamp = now;
                if (sha != null && string.Equals(sha, run.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    // The approved bytes again.
                    changed = run.FileChanged;
                    run.FileChanged = false;
                }
                else if (sha != null && TakesTrusted(run, trusted))
                {
                    // The run of the old bytes ends, and its end starts the
                    // new ones (OnExited).
                    TakeTrusted(run, sha, trusted);
                    StopRun(run);
                    changed = adopted = true;
                }
                else if (!run.FileChanged)
                {
                    run.FileChanged = true;
                    AddLog(run, new PluginLogLine(_clock(), PluginLogKind.ChangedWhileRunning));
                    changed = true;
                }
            }
            RunChecks();
            if (adopted) SavedChanged?.Invoke();
            if (changed) Changed?.Invoke();
        }

        /// <summary>
        /// Ends each plugin that has not said hello
        /// <see cref="HelloDeadlineMs"/> after its start, unless a debugger is
        /// attached to it, as <see cref="EndSilent"/> spares one stopped at a
        /// breakpoint. Its key is taken, so a hello that comes late is
        /// refused, and it is marked <see cref="PluginRun.NeverConnected"/>.
        /// Its end is not a stop of PadForge's: the back-off starts it again
        /// (OnExited), and a run with no hello never counts as a steady one.
        /// The page's tick calls it once a second.
        /// </summary>
        public void CheckHello()
        {
            var late = new List<IPluginProcess>();
            lock (_sync)
            {
                long now = _nowMs();
                foreach (var run in _runs)
                {
                    if (run.Process == null || run.Token == null || run.TokenTaken || run.Stopping) continue;
                    if (now - run.StartedAtMs < HelloDeadlineMs) continue;
                    bool debugged;
                    try { debugged = run.Process.DebuggerAttached; }
                    catch { debugged = false; }
                    if (debugged) continue;
                    run.Token = null;
                    run.NeverConnected = true;
                    AddLog(run, new PluginLogLine(_clock(), PluginLogKind.NeverConnected));
                    late.Add(run.Process);
                }
            }
            foreach (var process in late)
            {
                try { process.Stop(); } catch { }
            }
            if (late.Count > 0) Changed?.Invoke();
        }

        /// <summary>
        /// A plugin that sent nothing for a minute is ended and started again
        /// with the back-off, unless a debugger is attached to it
        /// (CheckRemoteDebuggerPresent): a plugin stopped at a breakpoint is
        /// silent by its author's hand. True when it was ended.
        /// </summary>
        public bool EndSilent(PluginRun run)
        {
            IPluginProcess process;
            lock (_sync)
            {
                if (!_runs.Contains(run) || run.Process == null || run.Stopping) return false;
                process = run.Process;
                bool debugged;
                try { debugged = process.DebuggerAttached; }
                catch { debugged = false; }
                if (debugged) return false;
                AddLog(run, new PluginLogLine(_clock(), PluginLogKind.Silent));
                run.Token = null;
            }
            // Not a stop of PadForge's: its end counts as a failure, so the
            // back-off starts it again (OnExited).
            try { process.Stop(); } catch { }
            Changed?.Invoke();
            return true;
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
