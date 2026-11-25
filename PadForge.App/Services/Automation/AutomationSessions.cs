using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// The programs on the Automation pipe (#597) and what they own: their
    /// devices, their holds on other devices' controls, the values they hand
    /// to macro triggers and what they follow. Every line a session reads is
    /// run here, at the level in force when it runs.
    ///
    /// <para>Limits: <see cref="MostSessions"/> sessions, of which
    /// <see cref="KeptForPlugins"/> only plugins take, and
    /// <see cref="MostPerProcess"/> for one process.
    /// <see cref="MostWaitingForHello"/> connections that have not said
    /// hello, the oldest let go when another comes. 8 devices a session and
    /// 32 in all (<see cref="ProgramDevices"/>).
    /// <see cref="MostSubscriptions"/> subscriptions a session. And the
    /// line rate and length a session keeps itself.</para>
    ///
    /// <para>A session that holds or claims a control or drives a device
    /// keeps a lease: it sends a line at least every 250 ms for a script,
    /// 2,000 ms for any other program, or the span its hello asked for. A
    /// line that runs keeps it meanwhile. When it runs out, the holds and
    /// claims let go at the first pass past the span, judged on the clock
    /// that applies them (ControlHolds.Lease), and at the sessions' clock
    /// while no pass runs. A script's come back at its next line, any other
    /// program's go for good. The sessions' clock then rests the devices and
    /// tells the program. A script silent for 5 s has its devices taken
    /// offline until it speaks (#600 item 24). A session that only reads
    /// has none. The pipe closing is the usual sign that a program ended.
    /// The lease catches the one that hangs.</para>
    /// </summary>
    internal sealed class AutomationSessions : IDisposable
    {
        internal const int MostSessions = 16;
        internal const int KeptForPlugins = 4;
        internal const int MostPerProcess = 4;
        internal const int MostWaitingForHello = 32;
        internal const int MostSubscriptions = 256;

        /// <summary>How often the sessions' clock runs: leases, devices
        /// waiting to go offline, Activity's runs, the engine and the
        /// profile.</summary>
        internal const int ClockMs = 100;

        /// <summary>How long a connection may wait before its hello.</summary>
        internal const int HelloWithinMs = 5000;

        /// <summary>How long a run of the same line waits for more before it
        /// is written to Activity as one entry with its count.</summary>
        internal const int RunGapMs = 1000;

        private readonly AutomationHost _host;
        private readonly IAutomationUi _ui;
        private readonly Func<long> _nowMs;
        private readonly object _sync = new object();
        private readonly List<AutomationSession> _sessions = new();
        private readonly LinkedList<AutomationSession> _waiting = new();

        /// <summary>The connections handed over that are no program of the
        /// list: the web helper's own (#600 item 11). They count nowhere,
        /// and close with the rest.</summary>
        private readonly List<AutomationSession> _settled = new();
        private readonly Dictionary<AutomationSession, Owned> _owned = new();
        private readonly AutomationHolds _held = new();
        private readonly Timer _clock;

        /// <summary>The last session's number. A number names its session's
        /// holds and claims in <see cref="ControlHolds"/> and its values in
        /// ScriptValues, each one table for the whole process, so it counts
        /// across every host the process makes and is never given twice.</summary>
        private static int _lastNumber;
        private int _disposed;
        private bool _clockOn;
        private AutomationEngineState? _engineSeen;
        private string _profileSeen = "\0";

        /// <summary>What one session has, apart from its devices.</summary>
        private sealed class Owned
        {
            public readonly List<AutomationWatch.Entry> Subscriptions = new();
            public bool Devices, Profile, Engine;
            public AutomationAsk RunAsk;
            public string RunResult;
            public bool RunOk;
            public int RunCount;
            public long RunLastMs;
            public DateTimeOffset RunTime;
        }

        public AutomationSessions(AutomationHost host, IAutomationUi ui, Func<long> nowMs = null)
        {
            _host = host;
            _ui = ui;
            _nowMs = nowMs ?? (() => Environment.TickCount64);
            Devices = new ProgramDevices(_nowMs);
            _clock = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public AutomationHost Host => _host;
        public ProgramDevices Devices { get; }

        /// <summary>The programs the user allowed (#600). The app's own list
        /// unless a test gives one.</summary>
        internal AutomationConsent Consent { get; set; } = AutomationConsent.Shared;

        /// <summary>Raised when a session's own level changes because the
        /// user answered its first connection or removed its consent (#600),
        /// on the thread that took the answer.</summary>
        internal event Action<AutomationSession> SessionLevelChanged;
        public AutomationWatch Watch { get; } = new AutomationWatch();

        /// <summary>The sessions that said hello, now.</summary>
        public int Count
        {
            get { lock (_sync) return _sessions.Count; }
        }

        /// <summary>The sessions open now, for tests and the
        /// interface.</summary>
        internal List<AutomationSession> Snapshot()
        {
            lock (_sync) return new List<AutomationSession>(_sessions);
        }

        // ── Connections ──

        /// <summary>One connection the pipe took, run to its end.</summary>
        internal async Task ServeAsync(NamedPipeServerStream stream, AutomationClientProcess process, CancellationToken stopping)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var session = new AutomationSession(this, stream, process, Interlocked.Increment(ref _lastNumber), stopping, _nowMs);
            // A connection that never says hello is let go, and so is the
            // oldest of those that wait when too many do: each holds a
            // buffer and a thread's worth of waiting.
            AutomationSession dropped = null;
            LinkedListNode<AutomationSession> node;
            lock (_sync)
            {
                if (_waiting.Count >= MostWaitingForHello)
                {
                    dropped = _waiting.First.Value;
                    _waiting.RemoveFirst();
                }
                node = _waiting.AddLast(session);
            }
            // The pipe's accept loop called this, so the list above holds the
            // connections in the order they came. The rest runs off that
            // loop.
            await Task.Yield();
            dropped?.Close("too-many");
            _ = Task.Delay(HelloWithinMs, session.Stopping).ContinueWith(t =>
            {
                if (!t.IsCanceled && !session.Opened) session.Close("no-hello");
            }, TaskScheduler.Default);
            try
            {
                await session.RunAsync().ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    if (node.List != null) _waiting.Remove(node);
                }
            }
        }

        /// <summary>How many connections wait for their hello, for
        /// tests.</summary>
        internal int Waiting
        {
            get { lock (_sync) return _waiting.Count; }
        }

        /// <summary>Lists a session whose hello was taken. False with the
        /// error's code when the sessions are full: all of them, the ones
        /// that are not kept for plugins, or those one process may
        /// hold.</summary>
        internal bool Open(AutomationSession session, out string error)
        {
            lock (_sync)
            {
                int ofProcess = 0, notPlugins = 0;
                foreach (var open in _sessions)
                {
                    // Each web assistant comes through the one web helper,
                    // which PadForge started: its sessions are bounded by the
                    // web assistants allowed, not by the cap of one process.
                    if (open.Process.Id == session.Process.Id && open.Kind != AutomationClientKind.Web) ofProcess++;
                    if (!open.IsPlugin) notPlugins++;
                }
                if (_sessions.Count >= MostSessions
                    || (!session.IsPlugin && notPlugins >= MostSessions - KeptForPlugins)
                    || (ofProcess >= MostPerProcess && session.Kind != AutomationClientKind.Web))
                {
                    error = "too-many";
                    return false;
                }
                _waiting.Remove(session);
                _sessions.Add(session);
                _owned[session] = new Owned();
                // The lease, judged where its holds and claims are applied.
                session.Lease = new ControlHolds.Lease(session.LeaseMs, comesBack: session.Kind == AutomationClientKind.Script);
                ControlHolds.Keep(session.Number, session.Lease);
                EnsureClock();
            }
            error = null;
            // Who the program is, read once: the connected list carries the
            // path of the program behind it, and consent keys by it. An
            // allowed program's last connection is noted before the list
            // changes, so the Automation page reads it with the change.
            if (!session.IsPlugin)
            {
                var who = session.Who ??= (Consent ?? AutomationConsent.Shared).Covering(AutomationConsent.Identify(session.Process, session.KindWord, session.ClaimedName));
                var store = Consent ?? AutomationConsent.Shared;
                if (store.Find(who.Key) != null) store.Used(who.Key, _host.Now());
            }
            session.Client = _host.Clients.Add(session.ClaimedName, session.Kind, session.Process.Id, session.Process.ImageName,
                session.GivenName, session.Who?.Path ?? session.Process.ImagePath, session.Who?.Script);
            Record(session, new AutomationAsk(AutomationAskKind.Connected, session.ClaimedName), "ok", true);
            return true;
        }

        /// <summary>A session ended, however it ended: its subscriptions,
        /// holds and values go, and its devices rest and go offline after
        /// the wait. Safe to call more than once.</summary>
        internal void End(AutomationSession session)
        {
            Owned owned;
            lock (_sync)
            {
                if (!_owned.TryGetValue(session, out owned))
                {
                    _sessions.Remove(session);
                    _settled.Remove(session);
                    return;
                }
                // Gone from what it owns first, and from the count of
                // sessions last, so a session no longer counted has let go
                // of everything.
                _owned.Remove(session);
                _held.ReleaseAll(session);
                ControlHolds.Forget(session.Number);
                PublishWatchLocked();
            }
            session.Close("ended");
            ScriptValues.ReleaseOwner(session.Number);
            Devices.SessionEnded(session, now: false);
            if (session.Client != null) _host.Clients.Remove(session.Client.Id);
            FlushRun(session, owned);
            Record(session, new AutomationAsk(AutomationAskKind.Disconnected), "ok", true, owned: null);
            lock (_sync) _sessions.Remove(session);
        }

        /// <summary>Whether the pipe's client is taken, from its token:
        /// null when it is, or the reason in a word.</summary>
        internal string Judge(NamedPipeServerStream stream) => _host.AutomationPipe.Judge(stream);

        // ── The clock ──

        private void EnsureClock()
        {
            if (_clockOn) return;
            _clockOn = true;
            try { _clock.Change(ClockMs, ClockMs); } catch (ObjectDisposedException) { }
        }

        /// <summary>The sessions' clock: leases, devices whose wait ran out,
        /// Activity's runs, and the engine and profile for their
        /// followers.</summary>
        internal void Tick()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            // A slow tick is never joined by the next one.
            if (Interlocked.Exchange(ref _ticking, 1) != 0) return;
            try { TickOnce(); }
            finally { Volatile.Write(ref _ticking, 0); }
        }

        private int _ticking;

        private void TickOnce()
        {
            long now = _nowMs();
            long ticks = ControlHolds.Now();
            List<AutomationSession> lapse = null, asleep = null;
            List<(AutomationSession, Owned)> runs = null;
            AutomationSession[] engineFollowers = null, profileFollowers = null;
            lock (_sync)
            {
                foreach (var session in _sessions)
                {
                    if (!_owned.TryGetValue(session, out var owned)) continue;
                    // The lease the holds table judges, on its clock.
                    if (!session.Lapsed && session.Lease is { } lease && lease.Silent(ticks)
                        && (_held.Count(session) > 0 || Devices.OwnedBy(session).Count > 0))
                        (lapse ??= new()).Add(session);
                    // A script silent for ScriptOfflineMs has its devices
                    // taken offline at rest until it speaks again (#600 item
                    // 24). A plugin's silence is the plugins' to judge, and
                    // an assistant's helper speaks for it.
                    if (session.Kind == AutomationClientKind.Script && !session.Asleep && !session.Handling
                        && now - session.LastHeardMs > ScriptOfflineMs && Devices.OwnedBy(session).Count > 0)
                        (asleep ??= new()).Add(session);
                    if (owned.RunCount > 0 && now - owned.RunLastMs >= RunGapMs) (runs ??= new()).Add((session, owned));
                }
                _held.Purge();
                engineFollowers = Followers(o => o.Engine);
                profileFollowers = Followers(o => o.Profile);
                // The clock runs while a program is connected or a device
                // of one that left waits to go offline.
                if (_sessions.Count == 0 && Devices.Live == 0 && _clockOn)
                {
                    _clockOn = false;
                    try { _clock.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
                }
            }
            // While no pass runs, a lapse lets go of the table here, before
            // the program hears of it. With passes, the first one past the
            // span did it already.
            ControlHolds.LandDue();
            if (lapse != null)
                foreach (var session in lapse)
                {
                    // A line that came since the look above keeps the lease.
                    if (session.Lease.Silent(ControlHolds.Now())) Lapse(session);
                }
            if (asleep != null)
                foreach (var session in asleep)
                {
                    // Under the session's own gate, which its next line's
                    // wake takes too: a line that came since the look above
                    // keeps the devices, and one that comes now wakes them
                    // after they went.
                    lock (session.SleepGate)
                    {
                        if (_nowMs() - session.LastHeardMs <= ScriptOfflineMs) continue;
                        session.Asleep = true;
                        Devices.Sleep(session);
                    }
                }
            if (runs != null) foreach (var (session, owned) in runs) FlushRun(session, owned);
            Devices.Tick();
            FollowEngine(engineFollowers);
            FollowProfile(profileFollowers);
        }

        private AutomationSession[] Followers(Func<Owned, bool> follows)
        {
            List<AutomationSession> list = null;
            foreach (var session in _sessions)
                if (_owned.TryGetValue(session, out var owned) && follows(owned)) (list ??= new()).Add(session);
            return list?.ToArray() ?? Array.Empty<AutomationSession>();
        }

        /// <summary>How long a script may say nothing before its devices go
        /// offline at rest (#600 item 24).</summary>
        internal int ScriptOfflineMs { get; set; } = 5000;

        /// <summary>The lease ran out: the devices rest and the program
        /// hears <c>e lease lapsed</c>. Its holds and claims let go where the
        /// table judges the lease (ControlHolds.Lease): a script's stay in
        /// the table's keeping and come back at its next line. Any other
        /// program's go for good here.</summary>
        internal void Lapse(AutomationSession session)
        {
            lock (_sync)
            {
                if (!_owned.ContainsKey(session)) return;
                session.Lapsed = true;
                // Any other program's holds go for good: an assistant's
                // helper speaks for it on every call, and a hold it let
                // lapse is not pressed again by its next one.
                if (session.Kind != AutomationClientKind.Script) _held.ReleaseAll(session);
            }
            Devices.Rest(session);
            session.Outbox.Line("e lease lapsed");
        }

        /// <summary>A session whose lease ran out, or whose devices went
        /// offline for its silence, spoke again, and its lease was renewed
        /// first: a script's holds and claims are back (ControlHolds.Renew),
        /// and so are its devices. Another program whose lease the table let
        /// lapse before the sessions' clock looked lets go of its holds for
        /// good now, before its line runs.</summary>
        internal void Woke(AutomationSession session)
        {
            bool lapsedNow = false;
            lock (_sync)
            {
                if (!_owned.ContainsKey(session)) return;
                if (!session.Lapsed && session.Kind != AutomationClientKind.Script && ControlHolds.IsOut(session.Number))
                {
                    _held.ReleaseAll(session);
                    lapsedNow = true;
                }
                session.Lapsed = false;
            }
            if (lapsedNow)
            {
                Devices.Rest(session);
                session.Outbox.Line("e lease lapsed");
            }
            lock (session.SleepGate)
            {
                if (!session.Asleep) return;
                session.Asleep = false;
                Devices.Wake(session);
            }
        }

        private void FollowEngine(AutomationSession[] followers)
        {
            var read = _host.EngineState;
            if (read == null) return;
            AutomationEngineState state;
            try { state = read(); } catch { return; }
            if (_engineSeen == state) return;
            bool first = _engineSeen == null;
            _engineSeen = state;
            if (first) return;
            foreach (var session in followers) session.Outbox.Line("e engine " + EngineWord(state));
        }

        internal static string EngineWord(AutomationEngineState state) => state switch
        {
            AutomationEngineState.Running => "running",
            AutomationEngineState.Idle => "idle",
            AutomationEngineState.Stopping => "stopping",
            _ => "stopped",
        };

        private void FollowProfile(AutomationSession[] followers)
        {
            string id = SettingsManager.ActiveProfileId ?? string.Empty;
            if (_profileSeen == id) return;
            bool first = _profileSeen == "\0";
            _profileSeen = id;
            if (first || followers.Length == 0) return;
            // The name is read where the profiles live.
            _ui.Post(() =>
            {
                string name = null;
                try { name = _host.ProfileName?.Invoke(); } catch { }
                string line = string.IsNullOrEmpty(name) ? "e profile default" : "e profile " + AutomationLine.Quote(AutomationReadScrub.Text(name));
                foreach (var session in followers) session.Outbox.Line(line);
            });
        }

        // ── The level ──

        /// <summary>
        /// Automation's switch or level changed. Every session hears
        /// <c>e level &lt;level&gt;</c>. Below Read nothing is followed. Below
        /// Send Inputs every hold and value is let go and the devices of
        /// scripts and assistants go offline. Off closes the sessions, those
        /// of plugins apart while Plugins serves them.
        /// </summary>
        internal void OnLevel(bool on, AutomationLevel level, bool pluginsServed)
        {
            var close = new List<AutomationSession>();
            var devicesGo = new List<AutomationSession>();
            lock (_sync)
            {
                foreach (var session in _sessions)
                {
                    if (!on && !(pluginsServed && session.IsPlugin)) close.Add(session);
                    // Each session hears its own level, which the user's caps.
                    session.Outbox.Line("e level " + (on ? AutomationLevels.Word(LevelOf(session)) : "off"));
                }
                DropBelowLocked(session => !on || LevelOf(session) < AutomationLevel.Read,
                    session => !on || LevelOf(session) < AutomationLevel.SendInputs, devicesGo);
                // A value is the program's while it may set one: below Send
                // Inputs for the user's level, every value goes at once.
                if (!on || level < AutomationLevel.SendInputs) ScriptValues.ReleaseAll();
            }
            foreach (var session in devicesGo) Devices.SessionEnded(session, now: true);
            foreach (var session in close) session.CloseAfterWritten();
            // The web helper's connection carries no lines of the protocol
            // to write first: it closes at once.
            if (!on) foreach (var session in SettledNow()) session.Close("off");
        }

        /// <summary>What a session no longer allowed lets go of: below Read
        /// what it follows, below Send Inputs its holds, values and devices.
        /// A plugin keeps its own while the pipe serves it. Under the lock a
        /// value is set in (Value), so a line that passed the old level's
        /// check before this cannot set its value after it.</summary>
        private void DropBelowLocked(Func<AutomationSession, bool> belowRead, Func<AutomationSession, bool> belowSend, List<AutomationSession> devicesGo)
        {
            bool watch = false;
            foreach (var (session, owned) in _owned)
            {
                if (session.IsPlugin) continue;
                if (belowRead(session) && (owned.Subscriptions.Count > 0 || owned.Devices || owned.Profile || owned.Engine))
                {
                    owned.Subscriptions.Clear();
                    owned.Devices = owned.Profile = owned.Engine = false;
                    watch = true;
                }
                if (belowSend(session))
                {
                    // Claims need Send Inputs: they go with the holds, and
                    // a subscription that stays follows without its claim.
                    _held.ReleaseAll(session);
                    foreach (var entry in owned.Subscriptions) entry.Claims.Clear();
                    ScriptValues.ReleaseOwner(session.Number);
                    devicesGo.Add(session);
                }
            }
            if (watch) PublishWatchLocked();
        }

        // ── Consent per program (#600) ──

        /// <summary>The level a session works at now: the level it was
        /// granted, capped by the user's. A plugin works at the user's.</summary>
        internal AutomationLevel LevelOf(AutomationSession session)
        {
            var cap = _host.Level;
            if (session == null || session.IsPlugin) return cap;
            return (AutomationLevel)Math.Min((int)session.Granted, (int)cap);
        }

        /// <summary>Whether a session's first connection waits for the
        /// user's answer.</summary>
        internal bool ConsentWaits(AutomationSession session)
            => session?.Consent != null && !session.Consent.IsCompleted;

        /// <summary>Waits for the user's answer to a session's first
        /// connection, or for <paramref name="stopping"/>.</summary>
        internal async Task ConsentAnswered(AutomationSession session, CancellationToken stopping)
        {
            var consent = session?.Consent;
            if (consent == null || consent.IsCompleted) return;
            try { await consent.WaitAsync(stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        /// <summary>The answers being asked for, by the program's key, so a
        /// program that connects again while its card shows asks no second
        /// time.</summary>
        private readonly Dictionary<string, Task<bool>> _asking = new(StringComparer.Ordinal);

        /// <summary>The programs declined in this run, by key, with when they
        /// may ask again: not in this run after Don't Allow, ten minutes after
        /// a card nobody answered.</summary>
        private readonly Dictionary<string, DateTimeOffset> _declined = new(StringComparer.Ordinal);

        /// <summary>How long a program whose card nobody answered waits
        /// before it asks again.</summary>
        internal static readonly TimeSpan UnansweredQuiet = TimeSpan.FromMinutes(10);

        /// <summary>The resume secrets of assistants' helpers, by secret:
        /// the program's key and the caller key of its first session.</summary>
        private readonly Dictionary<string, (string Who, int Key)> _resumes = new(StringComparer.Ordinal);

        /// <summary>
        /// Decides a session's level as it opens (#600). A plugin works at
        /// the user's level. A program that asks for Commands, or for a level
        /// the user allowed it before, has it at once. Any other waits on the
        /// question card, at Commands meanwhile, for the user's answer, which
        /// names its path, its signer and for a script its interpreter and
        /// script file. One card per program: a program that connects again
        /// while it shows, or after the user declined in this run, asks no
        /// second time.
        /// </summary>
        internal void BeginConsent(AutomationSession session)
        {
            session.CallerKey = session.Number;
            if (session.IsPlugin) { session.Granted = _host.Level; return; }
            var wanted = session.Wanted ?? _host.Level;
            var who = session.Who ??= (Consent ?? AutomationConsent.Shared).Covering(AutomationConsent.Identify(session.Process, session.KindWord, session.ClaimedName));
            if (session.Resume != null)
            {
                lock (_sync)
                {
                    if (_resumes.TryGetValue(session.Resume, out var known)) { if (known.Who == who.Key) session.CallerKey = known.Key; }
                    else _resumes[session.Resume] = (who.Key, session.Number);
                }
            }
            if (wanted <= AutomationLevel.Commands) { session.Granted = AutomationLevel.Commands; return; }
            var store = Consent ?? AutomationConsent.Shared;
            var allowed = store.Find(who.Key);
            if (allowed != null && allowed.Level >= wanted) { session.Granted = wanted; return; }
            session.Granted = allowed?.Level ?? AutomationLevel.Commands;
            Task<bool> answer;
            lock (_sync)
            {
                if (_declined.TryGetValue(who.Key, out var until) && _host.Now() < until) return;
                if (!_asking.TryGetValue(who.Key, out answer)) _asking[who.Key] = answer = AskConsent(session, who, wanted, store);
            }
            session.Consent = answer;
        }

        /// <summary>
        /// Decides a web assistant's level as it opens (#600 item 11): the
        /// user allowed it at its sign-in, on the question card with its
        /// code, so it works at the user's level at once and asks nothing.
        /// Its helper's resume secret keeps its undo and rate across a pipe
        /// drop, as an assistant's does.
        /// </summary>
        internal void BeginWeb(AutomationSession session)
        {
            session.CallerKey = session.Number;
            string key = session.Who?.Key;
            if (session.Resume != null && key != null)
            {
                lock (_sync)
                {
                    if (_resumes.TryGetValue(session.Resume, out var known)) { if (known.Who == key) session.CallerKey = known.Key; }
                    else _resumes[session.Resume] = (key, session.Number);
                }
            }
            session.Granted = AutomationLevel.ChangeSettings;
        }

        /// <summary>A connection whose hello was taken but which is no
        /// program of the list, the web helper's own (#600 item 11): it no
        /// longer counts among those that wait for a hello, and it is closed
        /// when Automation goes off or the host stops, so the helper's pipe
        /// ends with PadForge's side of it.</summary>
        internal void Settled(AutomationSession session)
        {
            lock (_sync)
            {
                _waiting.Remove(session);
                _settled.Add(session);
            }
        }

        /// <summary>Closes the open sessions of a web assistant the user
        /// revoked (#600 item 11).</summary>
        internal void CloseWeb(string key)
        {
            List<AutomationSession> close = new();
            lock (_sync)
                foreach (var session in _sessions)
                    if (session.Kind == AutomationClientKind.Web && session.Who?.Key == key) close.Add(session);
            foreach (var session in close) session.CloseAfterWritten();
        }

        private async Task<bool> AskConsent(AutomationSession session, AllowedProgram who, AutomationLevel wanted, AutomationConsent store)
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            string levelName = wanted switch
            {
                AutomationLevel.Read => s.Extras_Automation_Level_Read,
                AutomationLevel.SendInputs => s.Extras_Automation_Level_SendInputs,
                _ => s.Extras_Automation_Level_ChangeSettings,
            };
            var detail = new List<string>();
            if (who.Kind is "assistant" or "command-line")
                detail.Add(string.Format(culture, s.Automation_Consent_Started_Format, who.Path ?? "?"));
            else if (who.Script != null)
                detail.Add(string.Format(culture, s.Automation_Consent_Running_Format, System.IO.Path.GetFileName(who.Path ?? string.Empty), who.Script));
            else if (who.Path != null)
                detail.Add(who.Path);
            // The card's From line names a script's interpreter and its
            // signer. For PadForge's own helper it names PadForge.exe, so the
            // program behind it is named with its own signer.
            if (who.Kind is "assistant" or "command-line")
                detail.Add(string.IsNullOrEmpty(who.Signer) ? s.Automation_Consent_NotSigned : string.Format(culture, s.Automation_Consent_Signed_Format, who.Signer));
            detail.Add(s.Automation_Consent_ReadsKeys);
            detail.Add(s.Automation_Consent_AsksOnce);
            var request = new ApprovalRequest(session.Program,
                string.Format(culture, s.Automation_Ask_Consent_Format, who.Name ?? session.ClaimedName, levelName), detail)
            { Kind = ApprovalKind.Consent };
            ApprovalAnswer answer;
            try
            {
                // Not tied to the connection: a program that connects again
                // while the card shows waits on the same answer.
                answer = await _host.Approvals.AskAsync(request, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                answer = ApprovalAnswer.TimedOut;
            }
            lock (_sync)
            {
                _asking.Remove(who.Key);
                if (answer == ApprovalAnswer.Denied) _declined[who.Key] = DateTimeOffset.MaxValue;
                else if (answer != ApprovalAnswer.Allowed) _declined[who.Key] = _host.Now() + UnansweredQuiet;
            }
            if (answer != ApprovalAnswer.Allowed) return false;
            store.Allow(who with { Level = wanted, Allowed = _host.Now(), LastUsed = _host.Now() });
            // Every open session of the program works at its level before
            // the answer is out, so a line that waited for it runs at it.
            Raise(who.Key, wanted);
            return true;
        }

        /// <summary>Every open session of a program the user just allowed
        /// works at what it asked for, and hears its level.</summary>
        private void Raise(string key, AutomationLevel wanted)
        {
            var raised = new List<AutomationSession>();
            lock (_sync)
            {
                foreach (var session in _sessions)
                {
                    if (session.IsPlugin || session.Who?.Key != key) continue;
                    var level = session.Wanted ?? wanted;
                    if (level <= session.Granted) continue;
                    session.Granted = level;
                    raised.Add(session);
                }
            }
            foreach (var session in raised)
            {
                if (_host.IsOn) session.Outbox.Line("e level " + AutomationLevels.Word(LevelOf(session)));
                try { SessionLevelChanged?.Invoke(session); } catch { /* a listener's failure is not the program's */ }
            }
        }

        /// <summary>The consent keys of the programs connected now, plugins
        /// left out.</summary>
        internal HashSet<string> ConnectedKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            lock (_sync)
                foreach (var session in _sessions)
                    if (!session.IsPlugin && session.Who?.Key is { } key) keys.Add(key);
            return keys;
        }

        /// <summary>Takes a program's consent away now (#600): the user
        /// removed it from Allowed Programs. Its open sessions drop to
        /// Commands and let go of what that level no longer allows.</summary>
        internal void Revoke(string key)
        {
            (Consent ?? AutomationConsent.Shared).Remove(key);
            var devicesGo = new List<AutomationSession>();
            var lowered = new List<AutomationSession>();
            lock (_sync)
            {
                foreach (var session in _sessions)
                {
                    if (session.IsPlugin || session.Who?.Key != key || session.Granted <= AutomationLevel.Commands) continue;
                    session.Granted = AutomationLevel.Commands;
                    lowered.Add(session);
                }
                if (lowered.Count > 0) DropBelowLocked(s => lowered.Contains(s), s => lowered.Contains(s), devicesGo);
            }
            foreach (var session in devicesGo) Devices.SessionEnded(session, now: true);
            foreach (var session in lowered)
            {
                if (_host.IsOn) session.Outbox.Line("e level " + AutomationLevels.Word(LevelOf(session)));
                try { SessionLevelChanged?.Invoke(session); } catch { /* a listener's failure is not the program's */ }
            }
        }

        // ── The engine ──

        internal void AttachEngine(IAutomationEngine engine)
        {
            Devices.Attach(engine);
        }

        internal void DetachEngine()
        {
            Devices.Detach();
            Watch.EngineStopped();
        }

        // ── Lines ──

        /// <summary>Runs one line of an open session.</summary>
        internal async Task RunAsync(AutomationSession session, AutomationRequest r)
        {
            // A line past Commands waits for the user's answer to the
            // program's first connection (#600), as long as the card does.
            if (ConsentWaits(session) && !IsCommandsLine(r.Verb))
                await ConsentAnswered(session, session.Stopping).ConfigureAwait(false);
            switch (r.Verb)
            {
                case "state": case "activate": case "deactivate": case "query": case "engine":
                case "controllers": case "slot": case "show": case "hide": case "minimize":
                case "snapshot": case "record": case "version": case "exit":
                    RunCommand(session, r);
                    return;

                case "devices": ListDevices(session, r); return;
                case "caps": Caps(session, r); return;
                case "read": Read(session, r); return;
                case "outputs": Outputs(session, r); return;
                case "sub": Subscribe(session, r); return;
                case "unsub": Unsubscribe(session, r); return;
                case "declare": Declare(session, r); return;
                case "s": Set(session, r); return;
                case "gone": Gone(session, r); return;
                case "rest": RestDevice(session, r); return;
                case "own": Own(session, r, untilMoved: false); return;
                case "set": Own(session, r, untilMoved: true); return;
                case "rel": Release(session, r); return;
                case "var": Value(session, r); return;
                case "call": await CallAsync(session, r).ConfigureAwait(false); return;

                case "log":
                    // A plugin's own verb: a line for its log on the Plugins
                    // page. Every other program is answered as for a verb
                    // PadForge does not know.
                    if (!session.IsPlugin) goto default;
                    WriteLog(session, r);
                    return;
                case "setting":
                    // A plugin's declared settings (#600 item 17), kept by the
                    // plugins' part. Every other program is answered as for a
                    // verb PadForge does not know.
                    if (session.IsPlugin && PadForge.Services.Plugins.PluginLines.Run(session, r)) return;
                    goto default;
                case "beat":
                    if (r.Tag != null) session.Answer(r.Tag, "ok");
                    return;
                case "bye":
                    session.Answer(r.Tag, "ok bye");
                    session.CloseAfterWritten();
                    return;
                case "hello":
                    session.Answer(r.Tag, AutomationLine.Error("bad-argument", "the session is open already"));
                    return;
                default:
                    session.Answer(r.Tag, AutomationLine.Error("unknown-command"));
                    Record(session, new AutomationAsk(AutomationAskKind.Unknown, r.Verb), "err unknown-command", false);
                    return;
            }
        }

        /// <summary>A line Commands allows, which never waits for
        /// consent.</summary>
        private static bool IsCommandsLine(string verb) => verb is "state" or "activate" or "deactivate" or "query" or "engine"
            or "controllers" or "slot" or "show" or "hide" or "minimize" or "snapshot" or "record" or "version" or "exit"
            or "beat" or "bye" or "hello" or "log";

        /// <summary>The longest <c>log</c> line a plugin's log keeps. The
        /// rest of a longer one is cut.</summary>
        internal const int LongestLogLine = 500;

        /// <summary><c>log "text"</c>: the plugin's words, cleaned of control
        /// characters and cut at <see cref="LongestLogLine"/>, go to its log.
        /// Answered only when the line carries a tag, as <c>beat</c> is, so
        /// a chatty plugin does not read an answer per line. Activity does
        /// not list it: the log is the plugin's own.</summary>
        private static void WriteLog(AutomationSession session, AutomationRequest r)
        {
            string text = ProgramDeviceDeclaration.Clean(string.Join(" ", r.Bare()), LongestLogLine);
            try { session.Grant?.Log?.Invoke(text); }
            catch { /* a listener's failure is not the plugin's */ }
            if (r.Tag != null) session.Answer(r.Tag, "ok");
        }

        /// <summary>Test seam: runs between a line's first check of the
        /// level and the work it does, where a level that drops in that
        /// moment lands. Null in the app.</summary>
        internal Action AfterTheLevelCheck;

        /// <summary>Whether the session may do what needs
        /// <paramref name="needed"/>. A plugin declares, drives and removes
        /// its own devices at any level while the pipe serves it.</summary>
        private bool Allows(AutomationSession session, AutomationLevel needed, bool pluginToo = false)
            => (pluginToo && session.IsPlugin) || (_host.IsOn && LevelOf(session) >= needed);

        private bool Refuse(AutomationSession session, AutomationRequest r, AutomationAskKind kind, string argument = null)
        {
            string line = AutomationLine.Error("not-allowed");
            session.Answer(r.Tag, line);
            Record(session, new AutomationAsk(kind, argument), line, false);
            return true;
        }

        private void Fail(AutomationSession session, AutomationRequest r, AutomationAskKind kind, string argument, string code, string words = null)
        {
            string line = AutomationLine.Error(code, words);
            session.Answer(r.Tag, line);
            Record(session, new AutomationAsk(kind, argument), line, false);
        }

        private void Done(AutomationSession session, AutomationRequest r, AutomationAskKind kind, string argument, string line = "ok", bool quiet = false)
        {
            if (!quiet || r.Tag != null) session.Answer(r.Tag, line);
            Record(session, new AutomationAsk(kind, argument), "ok", true);
        }

        /// <summary>The command set, in the words the control pipe takes,
        /// run by the core's executor at the level it asks itself.</summary>
        private void RunCommand(AutomationSession session, AutomationRequest r)
        {
            var words = new StringBuilder(r.Verb);
            foreach (var word in r.Words) words.Append(' ').Append(word.IsPair ? word.Raw : word.Value);
            string body = words.ToString();
            ExternalControlService.Answer answer;
            try
            {
                answer = _host.Commands.Answer(body, session.Stopping, session.Program);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                answer = new ExternalControlService.Answer(AutomationCommands.Internal);
            }
            string line = AutomationLine.FromCommandSet(answer.Line);
            session.Answer(r.Tag, line, answer.AfterWritten);
            var ask = AutomationCommands.Describe(body);
            bool ok = line.StartsWith("ok", StringComparison.Ordinal);
            Record(session, ask, ok && ask.Kind == AutomationAskKind.State ? "ok" : line, ok);
        }

        // ── Read ──

        /// <summary>A device by its short ID: its row, or the entry of a
        /// program device that has no row yet.</summary>
        internal (UserDevice Row, ProgramDevices.Entry Entry) FindDevice(string id)
        {
            var entry = Devices.Find(id);
            var devices = SettingsManager.UserDevices;
            if (devices != null && !string.IsNullOrEmpty(id))
            {
                lock (devices.SyncRoot)
                {
                    foreach (var ud in devices.Items)
                        if (ud != null && AutomationDevices.IdOf(ud.InstanceGuid) == id) return (ud, entry);
                }
            }
            return (null, entry);
        }

        /// <summary>Whether a device PadForge has is not connected, as a
        /// read finds it (Read): a row that is offline and is not a
        /// program's own device, which a read takes from the program's
        /// copy.</summary>
        private static bool NotConnected(UserDevice ud, ProgramDevices.Entry entry)
            => ud != null && !ud.IsOnline
            && !(entry != null && (ud.Device == null || ReferenceEquals(ud.Device, entry.Device)));

        private void ListDevices(AutomationSession session, AutomationRequest r)
        {
            if (!Allows(session, AutomationLevel.Read)) { Refuse(session, r, AutomationAskKind.Devices); return; }
            AutomationReadScrub.Prepare();
            var rows = new List<string>();
            var seen = new HashSet<Guid>();
            var devices = SettingsManager.UserDevices;
            if (devices != null)
            {
                lock (devices.SyncRoot)
                {
                    foreach (var ud in devices.Items)
                    {
                        if (ud == null || !seen.Add(ud.InstanceGuid)) continue;
                        rows.Add(AutomationDevices.Row(ud));
                    }
                }
            }
            // A program's device declared while the engine is stopped has
            // no row yet, and is listed from its own shape.
            foreach (var entry in Devices.All())
            {
                if (seen.Contains(entry.Guid)) continue;
                if (entry.Owner == null && entry.OfflineAtMs == 0 && !entry.BuiltIn) continue;
                rows.Add(AutomationDevices.Row(Stand(entry)));
            }
            foreach (var row in rows) session.Answer(r.Tag, row);
            Done(session, r, AutomationAskKind.Devices, null);
        }

        /// <summary>A row of a program device that has none on the list,
        /// made from its shape.</summary>
        private static UserDevice Stand(ProgramDevices.Entry entry)
        {
            var ud = new UserDevice();
            ud.LoadFromExternalDevice(entry.Device);
            ud.IsOnline = false;
            return ud;
        }

        private void Caps(AutomationSession session, AutomationRequest r)
        {
            string id = FirstBare(r);
            if (!Allows(session, AutomationLevel.Read)) { Refuse(session, r, AutomationAskKind.Caps, id); return; }
            var (ud, entry) = FindDevice(id);
            if (ud == null && entry == null) { Fail(session, r, AutomationAskKind.Caps, id, "unknown-device"); return; }
            AutomationReadScrub.Prepare();
            List<string> rows;
            if (ud != null)
            {
                lock (SettingsManager.UserDevices.SyncRoot) rows = AutomationDevices.Caps(ud, ScriptNames.Of(ud));
            }
            else
            {
                var stand = Stand(entry);
                rows = AutomationDevices.Caps(stand, ScriptNames.Of(stand));
            }
            foreach (var row in rows) session.Answer(r.Tag, row);
            Done(session, r, AutomationAskKind.Caps, id);
        }

        private void Read(AutomationSession session, AutomationRequest r)
        {
            string id = FirstBare(r);
            if (!Allows(session, AutomationLevel.Read)) { Refuse(session, r, AutomationAskKind.Read, id); return; }
            var (ud, entry) = FindDevice(id);
            CustomInputState state = null;
            int buttons = 0;
            if (entry != null && (ud == null || ud.Device == null || ReferenceEquals(ud.Device, entry.Device)))
            {
                state = entry.Device.PeekState().Clone();
                buttons = entry.Device.NumButtons;
            }
            else if (ud != null)
            {
                if (!ud.IsOnline) { Fail(session, r, AutomationAskKind.Read, id, "offline"); return; }
                state = AutomationDevices.CopyState(ud);
                buttons = Math.Max(ud.CapButtonCount, ud.CapButtonIndices is { Length: > 0 } b ? b[^1] + 1 : 0);
            }
            if (ud == null && entry == null) { Fail(session, r, AutomationAskKind.Read, id, "unknown-device"); return; }
            if (state == null) { Fail(session, r, AutomationAskKind.Read, id, "offline"); return; }
            Done(session, r, AutomationAskKind.Read, id, session.Outbox.Plain
                ? AutomationDevices.StateLine(state, buttons, RestsOf(ud, entry))
                : AutomationDevices.StateLine(state, buttons));
        }

        /// <summary>Each axis's rest on a device, which plain values are read
        /// and written against: what the device's own record gives, or the
        /// default rest where it names none (#600). Read under the device
        /// list's lock for a row.</summary>
        internal static int[] RestsOf(UserDevice ud, ProgramDevices.Entry entry)
        {
            var rests = AxisStreamSlots.DefaultRests();
            if (entry != null)
            {
                var shape = entry.Device.Shape;
                for (int i = 0; i < rests.Length; i++) rests[i] = RestOrDefault(shape, i);
                return rests;
            }
            if (ud == null) return rests;
            var list = SettingsManager.UserDevices;
            if (list != null)
                lock (list.SyncRoot) Fill(rests, ud);
            else Fill(rests, ud);
            return rests;

            static void Fill(int[] into, UserDevice row)
            {
                for (int i = 0; i < into.Length; i++)
                    if (AutomationDevices.RestOf(row, i) is int rest) into[i] = rest;
            }
        }

        private void Outputs(AutomationSession session, AutomationRequest r)
        {
            string text = FirstBare(r);
            if (!Allows(session, AutomationLevel.Read)) { Refuse(session, r, AutomationAskKind.Outputs, text); return; }
            if (!AutomationLine.TryInt(text, out int number) || number < 1 || number > 16)
            {
                Fail(session, r, AutomationAskKind.Outputs, text, "bad-argument", "outputs takes a controller's number, 1 to 16");
                return;
            }
            var engine = Devices.Engine;
            if (engine == null) { Fail(session, r, AutomationAskKind.Outputs, text, "engine-stopped"); return; }
            string line = engine.Outputs(number);
            if (line == null) { Fail(session, r, AutomationAskKind.Outputs, text, "unknown-slot"); return; }
            Done(session, r, AutomationAskKind.Outputs, text, "ok " + line);
        }

        private static string FirstBare(AutomationRequest r)
        {
            foreach (var word in r.Words) if (!word.IsPair) return word.Value;
            return string.Empty;
        }

        /// <summary>The controls of a device by the names scripts use
        /// (#600 item 21): of its row, or of a program device's shape while
        /// it has none.</summary>
        internal static List<ScriptControl> ControlsOf(UserDevice ud, ProgramDevices.Entry entry)
        {
            if (ud != null)
            {
                var list = SettingsManager.UserDevices;
                if (list == null) return ScriptNames.Of(ud);
                lock (list.SyncRoot) return ScriptNames.Of(ud);
            }
            return entry == null ? new List<ScriptControl>() : ScriptNames.Of(Stand(entry));
        }

        /// <summary>A line's timing (#600 item 22): <c>after=</c>, the ms a
        /// timed step waits after the session's previous one, or <c>in=</c>,
        /// the ms it waits from now. 0 from now with neither, -1 for both or
        /// for a value that is no number of ms up to a minute.</summary>
        private static int AfterOf(AutomationRequest r, out bool fromNow)
        {
            string after = r.Get("after"), within = r.Get("in");
            fromNow = after == null;
            if (after != null && within != null) return -1;
            string text = after ?? within;
            if (text == null) return 0;
            return AutomationLine.TryInt(text, out int ms) && ms >= 0 && ms <= AutomationHolds.LongestAfterMs ? ms : -1;
        }

        // ── Following ──

        private void Subscribe(AutomationSession session, AutomationRequest r)
        {
            var bare = r.Bare();
            string what = string.Join(" ", bare);
            if (!Allows(session, AutomationLevel.Read)) { Refuse(session, r, AutomationAskKind.Subscribe, what); return; }
            AfterTheLevelCheck?.Invoke();
            if (bare.Count == 1 && (bare[0] is "devices" or "profile" or "engine"))
            {
                bool followed;
                // The devices online as the program comes to follow them,
                // read before the sessions' lock: PadForge's first look after
                // tells what changed since (AutomationWatch.Baseline).
                var arriving = bare[0] == "devices" ? AutomationWatch.OnlineNow() : null;
                lock (_sync)
                {
                    if (!_owned.TryGetValue(session, out var owned)) return;
                    // The level again, under the lock a drop below Read
                    // clears what is followed in (OnLevel).
                    followed = Allows(session, AutomationLevel.Read);
                    if (followed)
                    {
                        if (bare[0] == "devices") owned.Devices = true;
                        else if (bare[0] == "profile") owned.Profile = true;
                        else owned.Engine = true;
                        PublishWatchLocked(arriving);
                    }
                }
                if (followed) Done(session, r, AutomationAskKind.Subscribe, what);
                else Refuse(session, r, AutomationAskKind.Subscribe, what);
                return;
            }
            if (bare.Count == 0) { Fail(session, r, AutomationAskKind.Subscribe, what, "bad-argument"); return; }
            string id = bare[0];
            var (ud, entry) = FindDevice(id);
            Guid device = ud?.InstanceGuid ?? entry?.Guid ?? Guid.Empty;
            if (device == Guid.Empty) { Fail(session, r, AutomationAskKind.Subscribe, what, "unknown-device"); return; }

            AutomationWatch.Entry made;
            string kind = bare.Count > 1 ? bare[1].ToLowerInvariant() : null;
            string axisText = r.Get("axis");
            int[] rests = RestsOf(ud, entry);
            if (axisText == null && kind != null && kind is not ("buttons" or "hats" or "axes"))
            {
                // A named subscription (#600 items 21 and 23).
                bool claim = false;
                for (int i = 2; i < bare.Count; i++)
                {
                    if (string.Equals(bare[i], "claim", StringComparison.OrdinalIgnoreCase)) claim = true;
                    else { Fail(session, r, AutomationAskKind.Subscribe, what, "bad-argument", "after the control comes claim alone: " + bare[i]); return; }
                }
                string named = NamedSubscription(session, r, id, device, ud, entry, bare[1], claim, rests, out made);
                if (named != null) { Fail(session, r, AutomationAskKind.Subscribe, what, "bad-argument", named); return; }
                AddSubscription(session, r, device, made, what);
                return;
            }
            if (axisText != null)
            {
                // above= and hyst= are raw, or plain against the axis's
                // rest (#600): above=80% on a stick, hyst=3%.
                if (!AutomationLine.TryInt(axisText, out int axis) || axis < 0 || axis >= ProgramAxis.End
                    || PlainUnits.ReadAxis(r.Get("above"), rests[axis], out int above) != null)
                {
                    Fail(session, r, AutomationAskKind.Subscribe, what, "bad-argument", "axis is 0 to 31 and above is 0 to 65535 or a percent");
                    return;
                }
                int hyst = 1024;
                string hystText = r.Get("hyst");
                if (hystText != null)
                {
                    bool read;
                    if (PlainUnits.TryParsePercent(hystText, out double share))
                    {
                        read = share >= 0 && share <= 100;
                        hyst = (int)Math.Round(share * PlainUnits.RawMax / 100);
                    }
                    else read = AutomationLine.TryInt(hystText, out hyst) && hyst >= 0 && hyst <= 65535;
                    if (!read)
                    {
                        Fail(session, r, AutomationAskKind.Subscribe, what, "bad-argument", "hyst is 0 to 65535 or a percent");
                        return;
                    }
                }
                made = new AutomationWatch.Crossing(session, device, id, axis, above, hyst);
                what = id + " axis=" + axis;
            }
            else if (kind == "buttons") made = new AutomationWatch.Buttons(session, device, id);
            else if (kind == "hats") made = new AutomationWatch.Hats(session, device, id);
            else if (kind == "axes")
            {
                int hz = 100;
                string hzText = bare.Count > 2 ? bare[2] : r.Get("hz");
                if (hzText != null && (!AutomationLine.TryInt(hzText, out hz) || hz < 1 || hz > 1000))
                {
                    Fail(session, r, AutomationAskKind.Subscribe, what, "bad-argument", "an axis stream runs 1 to 1000 times a second");
                    return;
                }
                made = new AutomationWatch.Axes(session, device, id, hz, rests);
            }
            else
            {
                Fail(session, r, AutomationAskKind.Subscribe, what, "bad-argument", "sub takes state, a control's name, buttons, hats, axes or axis=");
                return;
            }
            AddSubscription(session, r, device, made, what);
        }

        private int _lastLine;

        /// <summary>
        /// <c>sub &lt;id&gt; state</c>, <c>sub &lt;id&gt; A [claim]</c>,
        /// <c>sub &lt;id&gt; HatUp [claim]</c>, <c>sub &lt;id&gt; LeftX
        /// above=80% [claim]</c>, <c>sub &lt;id&gt; LeftX below=-80%</c>, and
        /// <c>sub &lt;id&gt; LeftX</c> for its values (#600 items 21 and 23).
        /// Each tells the device's whole state first. A claim takes the
        /// control from games while the subscription lives: a button reads
        /// up, a hat's direction is left out, and an axis past its line reads
        /// at its rest. Null when the line reads, or the words saying why
        /// not.
        /// </summary>
        private string NamedSubscription(AutomationSession session, AutomationRequest r, string id, Guid device,
            UserDevice ud, ProgramDevices.Entry entry, string word, bool claim, int[] rests, out AutomationWatch.Entry made)
        {
            made = null;
            if (claim && !Allows(session, AutomationLevel.SendInputs)) return "claim needs Send Inputs";
            int buttons = 0;
            if (ud != null) buttons = Math.Max(ud.CapButtonCount, ud.CapButtonIndices is { Length: > 0 } b ? b[^1] + 1 : 0);
            else if (entry != null) buttons = entry.Device.NumButtons;
            int hz = 100;
            string hzText = r.Get("hz");
            if (hzText != null && (!AutomationLine.TryInt(hzText, out hz) || hz < 1 || hz > 1000)) return "hz is 1 to 1000";
            if (string.Equals(word, "state", StringComparison.OrdinalIgnoreCase))
            {
                if (claim) return "a whole state is not claimed";
                made = new AutomationWatch.State(session, device, id, hz, rests, buttons);
                return null;
            }
            var controls = ControlsOf(ud, entry);
            string above = r.Get("above"), below = r.Get("below");
            if (ScriptNames.TryDirection(word, controls, out int hat, out var direction))
            {
                if (above != null || below != null) return "only an axis takes a line: " + word;
                made = new AutomationWatch.State(session, device, id, hz, rests, buttons, ('h', hat), word);
                if (claim) made.Claims.Add((ControlHolds.ClaimDirection, hat * 4 + DirectionIndex(direction), new object()));
                return null;
            }
            if (!ScriptNames.TryResolve(word, controls, out char kind, out int index)) return "no control is named " + word;
            if (kind == 'a' && (above != null || below != null))
            {
                if (above != null && below != null) return "a line is above= or below=, not both";
                if (PlainUnits.ReadAxis(above ?? below, rests[index], out int line) != null) return "the line is 0 to 65535 or a percent";
                int hyst = 1024;
                string hystText = r.Get("hyst");
                if (hystText != null)
                {
                    bool read;
                    if (PlainUnits.TryParsePercent(hystText, out double share))
                    {
                        read = share >= 0 && share <= 100;
                        hyst = (int)Math.Round(share * PlainUnits.RawMax / 100);
                    }
                    else read = AutomationLine.TryInt(hystText, out hyst) && hyst >= 0 && hyst <= 65535;
                    if (!read) return "hyst is 0 to 65535 or a percent";
                }
                int number = Interlocked.Increment(ref _lastLine);
                made = new AutomationWatch.Crossing(session, device, id, index, line, hyst, low: below != null, line: number, rests, buttons);
                if (claim) made.Claims.Add((ControlHolds.ClaimLine, number, new ControlHolds.Line(index, line, below == null, rests[index])));
                return null;
            }
            if (above != null || below != null) return "only an axis takes a line: " + word;
            if (claim && kind == 'a') return "an axis is claimed past a line, such as above=80%";
            made = new AutomationWatch.State(session, device, id, hz, rests, buttons, (kind, index), word);
            if (claim)
            {
                if (kind == 'b') made.Claims.Add((ControlHolds.ClaimButton, index, new object()));
                else
                    // A hat named whole: every direction is the script's.
                    for (int d = 0; d < 4; d++) made.Claims.Add((ControlHolds.ClaimDirection, index * 4 + d, new object()));
            }
            return null;
        }

        private static int DirectionIndex(HatDirections direction) => direction switch
        {
            HatDirections.Up => 0,
            HatDirections.Right => 1,
            HatDirections.Down => 2,
            _ => 3,
        };

        /// <summary>Puts a subscription in force, in place of one of the
        /// session's on the same device that names the same thing, and
        /// answers. A named one answers its line's number.</summary>
        private void AddSubscription(AutomationSession session, AutomationRequest r, Guid device, AutomationWatch.Entry made, string what)
        {
            lock (_sync)
            {
                if (!_owned.TryGetValue(session, out var owned)) return;
                // The level again, under the lock a drop below Read clears
                // what is followed in (OnLevel), and below Send Inputs every
                // claim.
                if (!Allows(session, AutomationLevel.Read) || (made.Claims.Count > 0 && !Allows(session, AutomationLevel.SendInputs)))
                {
                    Refuse(session, r, AutomationAskKind.Subscribe, what);
                    return;
                }
                RemoveSubscriptionsLocked(session, owned, e => e.Device == device && e.What == made.What);
                if (owned.Subscriptions.Count >= MostSubscriptions)
                {
                    PublishWatchLocked();
                    Fail(session, r, AutomationAskKind.Subscribe, what, "too-many", "a session follows up to 256 things");
                    return;
                }
                owned.Subscriptions.Add(made);
                foreach (var (kind, index, value) in made.Claims) _held.Claim(session, device, kind, index, value);
                PublishWatchLocked();
            }
            Done(session, r, AutomationAskKind.Subscribe, what,
                made is AutomationWatch.Crossing { Line: > 0 } line ? "ok line=" + line.Line : "ok");
        }

        /// <summary>Under the lock: takes the session's subscriptions that
        /// match away, and gives back what they claimed.</summary>
        private void RemoveSubscriptionsLocked(AutomationSession session, Owned owned, Predicate<AutomationWatch.Entry> match)
        {
            for (int i = owned.Subscriptions.Count - 1; i >= 0; i--)
            {
                var e = owned.Subscriptions[i];
                if (!match(e)) continue;
                owned.Subscriptions.RemoveAt(i);
                foreach (var (kind, index, value) in e.Claims) _held.Unclaim(session, e.Device, kind, index, value);
            }
        }

        private void Unsubscribe(AutomationSession session, AutomationRequest r)
        {
            var bare = r.Bare();
            string what = string.Join(" ", bare);
            if (!Allows(session, AutomationLevel.Read)) { Refuse(session, r, AutomationAskKind.Unsubscribe, what); return; }
            string axis = r.Get("axis");
            string line = r.Get("line");
            lock (_sync)
            {
                if (!_owned.TryGetValue(session, out var owned)) return;
                if (bare.Count == 1 && bare[0] == "all")
                {
                    RemoveSubscriptionsLocked(session, owned, _ => true);
                    owned.Devices = owned.Profile = owned.Engine = false;
                }
                else if (bare.Count == 1 && bare[0] == "devices") owned.Devices = false;
                else if (bare.Count == 1 && bare[0] == "profile") owned.Profile = false;
                else if (bare.Count == 1 && bare[0] == "engine") owned.Engine = false;
                else if (bare.Count >= 1)
                {
                    string id = bare[0];
                    string kind = line != null ? "line=" + line : axis != null ? "axis=" + axis : bare.Count > 1 ? bare[1].ToLowerInvariant() : null;
                    RemoveSubscriptionsLocked(session, owned, e => e.DeviceId == id && (kind == null || e.What == kind));
                }
                else
                {
                    PublishWatchLocked();
                    Fail(session, r, AutomationAskKind.Unsubscribe, what, "bad-argument");
                    return;
                }
                PublishWatchLocked();
            }
            Done(session, r, AutomationAskKind.Unsubscribe, what);
        }

        /// <summary>Under the lock: what every session follows, put in force
        /// on the poll thread's side.</summary>
        private void PublishWatchLocked(AutomationWatch.Baseline arriving = null)
        {
            var entries = new List<AutomationWatch.Entry>();
            var followers = new List<AutomationSession>();
            foreach (var session in _sessions)
            {
                if (!_owned.TryGetValue(session, out var owned)) continue;
                entries.AddRange(owned.Subscriptions);
                if (owned.Devices) followers.Add(session);
            }
            Watch.Publish(entries.ToArray(), followers.ToArray(), arriving);
        }

        // ── Devices of a program's own ──

        private void Declare(AutomationSession session, AutomationRequest r)
        {
            string key = r.Get("key");
            if (!Allows(session, AutomationLevel.SendInputs, pluginToo: true)) { Refuse(session, r, AutomationAskKind.Declare, key); return; }
            AfterTheLevelCheck?.Invoke();
            if (!ProgramDeviceDeclaration.IsKey(key))
            {
                Fail(session, r, AutomationAskKind.Declare, key, "bad-argument", "key is 1 to 64 letters, digits, _ . or -");
                return;
            }
            string name = ProgramDeviceDeclaration.Clean(r.Get("name"), ProgramDeviceDeclaration.LongestName);
            if (name.Length == 0) name = session.IdentityName.Length > 0 ? session.IdentityName + " " + key : key;
            var shape = ProgramDeviceDeclaration.Parse(r.Json, out string error);
            if (shape == null)
            {
                Fail(session, r, AutomationAskKind.Declare, key, "bad-declaration", error);
                return;
            }
            string code = Devices.Declare(session, key, name, shape, out var entry, out bool known);
            if (code != null)
            {
                Fail(session, r, AutomationAskKind.Declare, key, code);
                return;
            }
            // The level again, after the device stands: a drop that came
            // between the check above and the device being made took the
            // session's devices offline before this one was among them, and
            // it goes now. The level is set before the drop's work starts
            // (AutomationHost.Apply), so this read sees it.
            if (!Allows(session, AutomationLevel.SendInputs, pluginToo: true))
            {
                Devices.Gone(session, entry.Id);
                Refuse(session, r, AutomationAskKind.Declare, key);
                return;
            }
            Done(session, r, AutomationAskKind.Declare, name, "ok id=" + entry.Id);
            // What the device took last, for a program that comes back.
            if (known) foreach (var line in ProgramDevices.Replay(entry)) session.Outbox.Line(line);
        }

        private void Gone(AutomationSession session, AutomationRequest r)
        {
            string id = FirstBare(r);
            if (!Allows(session, AutomationLevel.SendInputs, pluginToo: true)) { Refuse(session, r, AutomationAskKind.Gone, id); return; }
            string code = Devices.Gone(session, id);
            if (code != null) { Fail(session, r, AutomationAskKind.Gone, id, code); return; }
            Done(session, r, AutomationAskKind.Gone, id);
        }

        /// <summary><c>rest &lt;id&gt;</c>: every control of the session's own
        /// device at rest in one word (#600). The device stays online.</summary>
        private void RestDevice(AutomationSession session, AutomationRequest r)
        {
            string id = FirstBare(r);
            if (!Allows(session, AutomationLevel.SendInputs, pluginToo: true)) { Refuse(session, r, AutomationAskKind.Rest, id); return; }
            var entry = Devices.Owned(session, id, out string code);
            if (entry == null) { Fail(session, r, AutomationAskKind.Rest, id, code); return; }
            entry.Device.Rest();
            Done(session, r, AutomationAskKind.Rest, id);
        }

        /// <summary><c>s &lt;id&gt; b3=1 a0=40000 h0=9000 ...</c>: several
        /// controls of the session's own device as one change. Answered only
        /// when it fails or carries a tag, so a stream at 1,000 lines a
        /// second does not come back as 1,000 answers.</summary>
        private void Set(AutomationSession session, AutomationRequest r)
        {
            string id = FirstBare(r);
            if (!Allows(session, AutomationLevel.SendInputs, pluginToo: true)) { Refuse(session, r, AutomationAskKind.Set, id); return; }
            var entry = Devices.Owned(session, id, out string code);
            if (entry == null) { Fail(session, r, AutomationAskKind.Set, id, code); return; }
            var change = new ProgramDeviceChange();
            var shape = entry.Device.Shape;
            var controls = ScriptNames.Declared(shape);
            string bad = ReadChange(r, change, i => shape.Axes.Count == 0 ? PlainUnits.DefaultRest(i) : RestOrDefault(shape, i), controls);
            int after = AfterOf(r, out bool fromNow);
            if (after < 0) bad ??= "after= or in= is 0 to 60000 ms, and one of them";
            if (bad != null) { Fail(session, r, AutomationAskKind.Set, id, "bad-argument", bad); return; }
            AfterTheLevelCheck?.Invoke();
            long due;
            lock (_sync) due = _held.Due(session, after, fromNow);
            entry.Device.Apply(change, due);
            // The level again, after the change: a drop that took the device
            // offline between the check above and the change leaves it at
            // rest, never with this line's controls pressed.
            var owner = entry.Owner;
            if (!Allows(session, AutomationLevel.SendInputs, pluginToo: true) || owner != session)
            {
                // A session of the same program that declared the device
                // since then drives it now, and its state is its own.
                if (owner == null || owner == session) entry.Device.Rest();
                Refuse(session, r, AutomationAskKind.Set, id);
                return;
            }
            Done(session, r, AutomationAskKind.Set, id, quiet: true);
        }

        /// <summary>The rest a device's shape gives an axis it declared, or
        /// the default rest of one it did not.</summary>
        internal static int RestOrDefault(ProgramDeviceShape shape, int index)
        {
            foreach (var axis in shape.Axes)
                if (axis.Index == index) return axis.Rest;
            return PlainUnits.DefaultRest(index);
        }

        /// <summary>Reads the controls of an <c>s</c> line. Null when every
        /// word reads, or the words naming the first that does not. A value
        /// is raw or plain (#600): a button 0, 1, true or false, an axis 0 to
        /// 65535 or a percent read against the axis's rest
        /// (<paramref name="restOf"/>, the default rests when null), a hat in
        /// hundredths, in degrees (<c>90deg</c>) or as directions
        /// (<c>Up+Right</c>). The bare word <c>rest</c> after the device puts
        /// every control at rest before the others apply.</summary>
        internal static string ReadChange(AutomationRequest r, ProgramDeviceChange change, Func<int, int> restOf = null,
            IReadOnlyList<ScriptControl> controls = null)
        {
            restOf ??= PlainUnits.DefaultRest;
            bool first = true;
            foreach (var word in r.Words)
            {
                if (!word.IsPair)
                {
                    if (first) { first = false; continue; }
                    if (string.Equals(word.Value, "rest", StringComparison.OrdinalIgnoreCase)) { change.Rest = true; continue; }
                    return "a control is name=value: " + word.Raw;
                }
                first = false;
                string key = word.Key.ToLowerInvariant();
                string value = word.Value;
                // The timed step's wait, which the verb reads.
                if (key is "after" or "in") continue;
                // A control by the name scripts use (#600 item 21), or its
                // short form.
                if (TryControl(key, out char kind, out int index) || ScriptNames.TryResolve(word.Key, controls, out kind, out index))
                {
                    switch (kind)
                    {
                        case 'b':
                            if (!PlainUnits.TryReadButton(value, out bool down)) return key + " is 0 or 1";
                            change.Buttons.Add((index, down));
                            break;
                        case 'a':
                        {
                            string why = PlainUnits.ReadAxis(value, restOf(index), out int raw);
                            if (why != null) return key + ": " + why;
                            change.Axes.Add((index, raw));
                            break;
                        }
                        case 'h':
                        {
                            string why = PlainUnits.ReadHat(value, out int raw);
                            if (why != null) return key + ": " + why;
                            change.Hats.Add((index, raw));
                            break;
                        }
                    }
                    continue;
                }
                switch (key)
                {
                    case "gyro":
                    case "accel":
                    {
                        var parts = value.Split(',');
                        if (parts.Length != 3) return key + " is x,y,z";
                        var xyz = new float[3];
                        for (int i = 0; i < 3; i++)
                            if (!AutomationLine.TryFloat(parts[i], out xyz[i])) return key + " is x,y,z";
                        if (key == "gyro") change.Gyro = xyz;
                        else change.Accel = xyz;
                        continue;
                    }
                    case "bat":
                        if (!AutomationLine.TryInt(value, out int battery) || battery < 0 || battery > 100) return "bat is 0 to 100";
                        change.Battery = battery;
                        continue;
                    case "charging":
                        if (value is not ("0" or "1")) return "charging is 0 or 1";
                        change.Charging = value == "1";
                        continue;
                }
                if (ScriptNames.TryDirection(word.Key, controls, out int dirHat, out var direction))
                {
                    // A hat's direction on its own (#600 item 21): it goes
                    // into the hat as the change lands, beside the hat's
                    // other directions.
                    if (!PlainUnits.TryReadButton(value, out bool pressed)) return key + " is 0 or 1";
                    change.Directions.Add((dirHat, direction, pressed));
                    continue;
                }
                if (key.StartsWith("tc", StringComparison.Ordinal) && AutomationLine.TryInt(key.Substring(2), out int clickPad)
                    && clickPad >= 0 && clickPad < ProgramDeviceDeclaration.MostTouchpads)
                {
                    if (value is not ("0" or "1")) return key + " is 0 or 1";
                    change.Clicks.Add((clickPad, value == "1"));
                    continue;
                }
                if (key.StartsWith("t", StringComparison.Ordinal))
                {
                    // t<pad>.<finger>=x,y,down[,pressure]
                    var at = key.Substring(1).Split('.');
                    if (at.Length == 2 && AutomationLine.TryInt(at[0], out int pad) && AutomationLine.TryInt(at[1], out int finger)
                        && pad >= 0 && pad < ProgramDeviceDeclaration.MostTouchpads && finger >= 0 && finger < ProgramDeviceDeclaration.MostFingers)
                    {
                        var parts = value.Split(',');
                        if (parts.Length is < 3 or > 4
                            || !AutomationLine.TryFloat(parts[0], out float x) || !AutomationLine.TryFloat(parts[1], out float y)
                            || parts[2] is not ("0" or "1"))
                            return key + " is x,y,down with x and y 0 to 1";
                        float pressure = 1f;
                        if (parts.Length == 4 && !AutomationLine.TryFloat(parts[3], out pressure)) return key + "'s pressure is 0 to 1";
                        change.Fingers.Add((pad, finger, x, y, parts[2] == "1", pressure));
                        continue;
                    }
                }
                return "no control is named " + key;
            }
            return null;
        }

        /// <summary><c>b&lt;i&gt;</c> a button below 256, <c>a&lt;i&gt;</c> an
        /// axis below 24 or a slider 24 to 31, <c>h&lt;i&gt;</c> a hat below
        /// 4.</summary>
        internal static bool TryControl(string key, out char kind, out int index)
        {
            kind = '\0';
            index = -1;
            if (key.Length < 2 || key[0] is not ('b' or 'a' or 'h')) return false;
            if (!int.TryParse(key.AsSpan(1), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out index)) return false;
            kind = key[0];
            int end = kind switch
            {
                'b' => CustomInputState.MaxButtons,
                'a' => ProgramAxis.End,
                _ => CustomInputState.MaxPovs,
            };
            return index >= 0 && index < end;
        }

        // ── Holds ──

        /// <summary><c>own &lt;id&gt; b3=1 a0=40000</c> or <c>own &lt;id&gt;
        /// A=1 LeftX=40%</c>: holds controls of any device at a value until
        /// let go. <c>set</c> (#600 item 22) puts the value there until the
        /// device's own input moves the control. <c>after=&lt;ms&gt;</c>
        /// waits that long after the session's previous timed step. A
        /// control another session holds refuses the whole line with
        /// <c>err busy</c>. A timed step, a tap's or a sequence's, on a
        /// device that is not connected is refused as a read of it is, with
        /// <c>err offline</c> (#600 item 21): fired later, at a moment the
        /// program did not choose, it is not what the program asked for. A
        /// hold waits for the device.</summary>
        private void Own(AutomationSession session, AutomationRequest r, bool untilMoved)
        {
            string id = FirstBare(r);
            if (!Allows(session, AutomationLevel.SendInputs)) { Refuse(session, r, AutomationAskKind.Own, id); return; }
            AfterTheLevelCheck?.Invoke();
            var (ud, entry) = FindDevice(id);
            Guid device = ud?.InstanceGuid ?? entry?.Guid ?? Guid.Empty;
            if (device == Guid.Empty) { Fail(session, r, AutomationAskKind.Own, id, "unknown-device"); return; }
            int after = AfterOf(r, out bool fromNow);
            if (after < 0) { Fail(session, r, AutomationAskKind.Own, id, "bad-argument", "after= or in= is 0 to 60000 ms, and one of them"); return; }
            if ((r.Get("after") != null || r.Get("in") != null) && NotConnected(ud, entry)) { Fail(session, r, AutomationAskKind.Own, id, "offline"); return; }
            var wanted = new List<(char Kind, int Index, int Value)>();
            int[] rests = null;
            List<ScriptControl> controls = null;
            foreach (var word in r.Words)
            {
                if (!word.IsPair) continue;
                string key = word.Key.ToLowerInvariant();
                if (key is "after" or "in") continue;
                // Raw or plain, as an s line takes them (#600), by short
                // form or by name.
                int v = 0;
                bool read = TryControl(key, out char kind, out int index)
                    || ScriptNames.TryResolve(word.Key, controls ??= ControlsOf(ud, entry), out kind, out index);
                if (read && kind == 'b')
                {
                    read = PlainUnits.TryReadButton(word.Value, out bool down);
                    v = down ? 1 : 0;
                }
                else if (read && kind == 'a')
                {
                    rests ??= RestsOf(ud, entry);
                    read = PlainUnits.ReadAxis(word.Value, rests[index], out v) == null;
                }
                else if (read) read = PlainUnits.ReadHat(word.Value, out v) == null;
                else if (ScriptNames.TryDirection(word.Key, controls ??= ControlsOf(ud, entry), out int hat, out var direction))
                {
                    // A hat's direction, held as a button is (#600 item 21):
                    // 1 presses it and 0 holds it released, over the hat's
                    // other directions.
                    kind = ControlHolds.HoldDirection;
                    index = hat * 4 + DirectionIndex(direction);
                    read = PlainUnits.TryReadButton(word.Value, out bool pressed);
                    v = pressed ? 1 : 0;
                }
                if (!read)
                {
                    Fail(session, r, AutomationAskKind.Own, id, "bad-argument",
                        "own and set take a control's name or b<i>=0|1, a<i>=0..65535 or a percent, h<i>=-1..35999, degrees or directions,"
                        + " or a hat's direction such as DPadUp=0|1: " + word.Raw);
                    return;
                }
                wanted.Add((kind, index, v));
            }
            if (wanted.Count == 0) { Fail(session, r, AutomationAskKind.Own, id, "bad-argument", "own names at least one control"); return; }
            string busy = null;
            lock (_sync)
            {
                if (!_owned.ContainsKey(session)) return;
                // The level again, under the lock the level's drop clears the
                // holds in (OnLevel): a line that passed the check above
                // before a drop is refused here, never held after it.
                if (Allows(session, AutomationLevel.SendInputs))
                {
                    busy = _held.Busy(session, device, wanted.ConvertAll(w => (w.Kind, w.Index)));
                    if (busy == null) _held.Hold(session, device, wanted, untilMoved, _held.Due(session, after, fromNow));
                }
                else busy = string.Empty;
            }
            if (busy == null) Done(session, r, AutomationAskKind.Own, id);
            else if (busy.Length == 0) Refuse(session, r, AutomationAskKind.Own, id);
            else
            {
                string line = AutomationLine.Error("busy", "another program holds " + busy);
                session.Answer(r.Tag, line);
                Record(session, new AutomationAsk(AutomationAskKind.Own, id), line, false);
            }
        }

        /// <summary><c>rel &lt;id&gt; b3 a0</c>, <c>rel &lt;id&gt; all</c> or
        /// <c>rel all</c>.</summary>
        private void Release(AutomationSession session, AutomationRequest r)
        {
            var bare = r.Bare();
            string id = bare.Count > 0 ? bare[0] : string.Empty;
            if (!Allows(session, AutomationLevel.SendInputs)) { Refuse(session, r, AutomationAskKind.Release, id); return; }
            int after = AfterOf(r, out bool fromNow);
            if (after < 0) { Fail(session, r, AutomationAskKind.Release, id, "bad-argument", "after= or in= is 0 to 60000 ms, and one of them"); return; }
            if (bare.Count == 1 && bare[0] == "all")
            {
                lock (_sync)
                {
                    if (!_owned.ContainsKey(session)) return;
                    _held.ReleaseAll(session, claimsToo: false);
                }
                Done(session, r, AutomationAskKind.Release, "all");
                return;
            }
            var (ud, entry) = FindDevice(id);
            Guid device = ud?.InstanceGuid ?? entry?.Guid ?? Guid.Empty;
            if (device == Guid.Empty) { Fail(session, r, AutomationAskKind.Release, id, "unknown-device"); return; }
            bool all = bare.Count == 2 && bare[1] == "all";
            var keys = new List<(char, int)>();
            if (!all)
            {
                List<ScriptControl> controls = null;
                for (int i = 1; i < bare.Count; i++)
                {
                    if (!TryControl(bare[i].ToLowerInvariant(), out char kind, out int index)
                        && !ScriptNames.TryResolve(bare[i], controls ??= ControlsOf(ud, entry), out kind, out index))
                    {
                        if (!ScriptNames.TryDirection(bare[i], controls, out int hat, out var direction))
                        {
                            Fail(session, r, AutomationAskKind.Release, id, "bad-argument",
                                "rel takes a control's name, b<i>, a<i>, h<i>, a hat's direction or all: " + bare[i]);
                            return;
                        }
                        kind = ControlHolds.HoldDirection;
                        index = hat * 4 + DirectionIndex(direction);
                    }
                    keys.Add((kind, index));
                }
                if (keys.Count == 0) { Fail(session, r, AutomationAskKind.Release, id, "bad-argument", "rel names a control or all"); return; }
            }
            lock (_sync)
            {
                if (!_owned.ContainsKey(session)) return;
                _held.Release(session, device, all ? _held.HeldOn(session, device) : keys, _held.Due(session, after, fromNow));
            }
            Done(session, r, AutomationAskKind.Release, id);
        }

        /// <summary>How many controls a session holds, for tests.</summary>
        internal int HoldsOf(AutomationSession session)
        {
            lock (_sync) return _owned.ContainsKey(session) ? _held.Holds(session) : 0;
        }

        /// <summary>How many controls a session holds and claims, for
        /// tests.</summary>
        internal int HeldAndClaimedBy(AutomationSession session)
        {
            lock (_sync) return _owned.ContainsKey(session) ? _held.Count(session) : 0;
        }

        // ── Named values ──

        /// <summary><c>var speed=42.5</c>: values Custom Expression macro
        /// triggers read by name. Several in one line are allowed.</summary>
        private void Value(AutomationSession session, AutomationRequest r)
        {
            var pairs = new List<(string, float)>();
            foreach (var word in r.Words)
            {
                if (!word.IsPair || !ScriptValues.IsName(word.Key) || !AutomationLine.TryFloat(word.Value, out float v))
                {
                    if (!Allows(session, AutomationLevel.SendInputs)) { Refuse(session, r, AutomationAskKind.Value, word.Raw); return; }
                    Fail(session, r, AutomationAskKind.Value, word.Raw, "bad-argument",
                        "var takes name=number, a name being a letter and up to 31 letters, digits, _ . or -");
                    return;
                }
                pairs.Add((word.Key, v));
            }
            string what = pairs.Count > 0 ? pairs[0].Item1 : string.Empty;
            if (!Allows(session, AutomationLevel.SendInputs)) { Refuse(session, r, AutomationAskKind.Value, what); return; }
            AfterTheLevelCheck?.Invoke();
            if (pairs.Count == 0) { Fail(session, r, AutomationAskKind.Value, what, "bad-argument", "var takes name=number"); return; }
            string full = null;
            bool allowed;
            lock (_sync)
            {
                // The level again, under the lock the level's drop lets go
                // of every value in (OnLevel): a line that passed the check
                // above before a drop sets nothing after it. A session that
                // ended sets nothing either, since its end lets go of what
                // it set once.
                allowed = _owned.ContainsKey(session) && Allows(session, AutomationLevel.SendInputs);
                if (allowed)
                {
                    foreach (var (name, value) in pairs)
                    {
                        if (ScriptValues.Set(name, value, session.Number)) continue;
                        full = name;
                        break;
                    }
                }
            }
            if (!allowed) { Refuse(session, r, AutomationAskKind.Value, what); return; }
            if (full != null)
            {
                Fail(session, r, AutomationAskKind.Value, full, "too-many", "PadForge keeps up to 256 value names");
                return;
            }
            Done(session, r, AutomationAskKind.Value, what);
        }

        // ── Tools ──

        private async Task CallAsync(AutomationSession session, AutomationRequest r)
        {
            string tool = FirstBare(r);
            var call = _host.Call;
            if (call == null) { Fail(session, r, AutomationAskKind.Call, tool, "not-available", "the assistant tools are not in this build"); return; }
            if (string.IsNullOrEmpty(tool)) { Fail(session, r, AutomationAskKind.Call, tool, "bad-argument", "call names a tool"); return; }
            AutomationCallResult result;
            try
            {
                result = await call(new AutomationCall(session.Peer(), tool, r.Json ?? "{}"), session.Stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                result = AutomationCallResult.Failure("internal");
            }
            // The tool's own path wrote the call into Activity, as it does
            // for an assistant's call, so the answer goes out alone.
            if (result == null || !result.Ok)
            {
                session.Answer(r.Tag, AutomationLine.Error(result?.Code ?? "internal", result?.Words));
                return;
            }
            string json = (result.Json ?? "{}").Replace("\r", string.Empty).Replace("\n", string.Empty);
            session.Answer(r.Tag, "ok " + json);
        }

        /// <summary>
        /// Runs one line of the session's for a tool an assistant called
        /// (#597), and gives the answer's lines with their tag taken off.
        /// The line goes through the same verb the program's own lines do,
        /// with the same checks. One such line runs at a time in a
        /// session.
        /// </summary>
        internal async Task<IReadOnlyList<string>> RunCapturedAsync(AutomationSession session, string body, CancellationToken stopping)
        {
            await session.CaptureGate.WaitAsync(stopping).ConfigureAwait(false);
            var lines = new List<string>();
            try
            {
                var r = AutomationLine.Parse(AutomationSession.CaptureTag + " " + body);
                if (r.Error != null) return new[] { AutomationLine.Error(r.Error) };
                session.Captured = lines;
                await RunAsync(session, r).ConfigureAwait(false);
                lock (lines) return lines.ToArray();
            }
            finally
            {
                session.Captured = null;
                session.CaptureGate.Release();
            }
        }

        // ── Activity ──

        /// <summary>
        /// Writes what a line asked into Activity. A run of the same ask with
        /// the same result is one entry with its count, written when the run
        /// ends or pauses for <see cref="RunGapMs"/>, so a stream of
        /// <c>s</c> lines is one entry and never one each.
        /// </summary>
        internal void Record(AutomationSession session, AutomationAsk ask, string result, bool ok)
        {
            // A line a tool runs for an assistant is part of the tool's
            // call, which writes its own entry (#597).
            if (session.Captured != null) return;
            Owned owned;
            lock (_sync) _owned.TryGetValue(session, out owned);
            Record(session, ask, result, ok, owned);
        }

        private void Record(AutomationSession session, AutomationAsk ask, string result, bool ok, Owned owned)
        {
            if (owned == null)
            {
                Add(session, ask, result, ok);
                return;
            }
            AutomationAsk flushAsk = default;
            string flushResult = null;
            bool flushOk = false;
            DateTimeOffset flushTime = default;
            bool flush = false;
            lock (owned)
            {
                if (owned.RunCount > 0 && owned.RunAsk.Kind == ask.Kind && owned.RunAsk.Argument == ask.Argument
                    && owned.RunResult == result)
                {
                    owned.RunCount++;
                    owned.RunLastMs = _nowMs();
                    return;
                }
                if (owned.RunCount > 0)
                {
                    flush = true;
                    flushAsk = owned.RunAsk with { Count = owned.RunCount };
                    flushResult = owned.RunResult;
                    flushOk = owned.RunOk;
                    flushTime = owned.RunTime;
                }
                owned.RunAsk = ask;
                owned.RunResult = result;
                owned.RunOk = ok;
                owned.RunCount = 1;
                owned.RunLastMs = _nowMs();
                owned.RunTime = _host.Now();
            }
            if (flush) Add(session, flushAsk, flushResult, flushOk, flushTime);
        }

        private void FlushRun(AutomationSession session, Owned owned)
        {
            if (owned == null) return;
            AutomationAsk ask;
            string result;
            bool ok;
            DateTimeOffset time;
            lock (owned)
            {
                if (owned.RunCount == 0) return;
                ask = owned.RunAsk with { Count = owned.RunCount };
                result = owned.RunResult;
                ok = owned.RunOk;
                time = owned.RunTime;
                owned.RunCount = 0;
            }
            Add(session, ask, result, ok, time);
        }

        private void Add(AutomationSession session, AutomationAsk ask, string result, bool ok, DateTimeOffset? time = null)
        {
            try
            {
                _host.Activity.Add(new AutomationActivityEntry(time ?? _host.Now(), session.Program, ask, result, ok));
            }
            catch
            {
                // A listener's failure is not the program's.
            }
        }

        // ── Closing ──

        /// <summary>Closes every session, as the pipe stops, and every
        /// connection handed over that is no program of the list.</summary>
        internal void CloseAll()
        {
            foreach (var session in Snapshot()) session.Close("stopped");
            foreach (var session in SettledNow()) session.Close("stopped");
        }

        private List<AutomationSession> SettledNow()
        {
            lock (_sync) return new List<AutomationSession>(_settled);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            CloseAll();
            _clock.Dispose();
            Watch.Unhook();
            // What its own sessions hold and claim goes now, and nothing of
            // another host's.
            lock (_sync)
                foreach (var session in new List<AutomationSession>(_owned.Keys))
                {
                    _held.ReleaseAll(session);
                    ControlHolds.Forget(session.Number);
                }
            Devices.Dispose();
        }
    }
}
