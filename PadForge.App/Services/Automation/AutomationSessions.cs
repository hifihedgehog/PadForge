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
    /// <para>A session that holds a control or drives a device keeps a
    /// lease: it sends a line at least every 2,000 ms, or the span its hello
    /// asked for, or its holds are let go and its devices rest until it
    /// speaks again. A session that only reads has none. The pipe closing is
    /// the usual sign that a program ended. The lease catches the one that
    /// hangs.</para>
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
        private readonly Dictionary<AutomationSession, Owned> _owned = new();
        private readonly Dictionary<(Guid Device, char Kind, int Index), (AutomationSession Owner, int Value)> _holds = new();
        private readonly Timer _clock;
        private int _lastNumber;
        private int _disposed;
        private bool _clockOn;
        private AutomationEngineState? _engineSeen;
        private string _profileSeen = "\0";

        /// <summary>What one session has, apart from its devices.</summary>
        private sealed class Owned
        {
            public readonly List<AutomationWatch.Entry> Subscriptions = new();
            public bool Devices, Profile, Engine;
            public int Holds;
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
                    if (open.Process.Id == session.Process.Id) ofProcess++;
                    if (!open.IsPlugin) notPlugins++;
                }
                if (_sessions.Count >= MostSessions
                    || (!session.IsPlugin && notPlugins >= MostSessions - KeptForPlugins)
                    || ofProcess >= MostPerProcess)
                {
                    error = "too-many";
                    return false;
                }
                _waiting.Remove(session);
                _sessions.Add(session);
                _owned[session] = new Owned();
                EnsureClock();
            }
            error = null;
            session.Client = _host.Clients.Add(session.ClaimedName, session.Kind, session.Process.Id, session.Process.ImageName);
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
                    return;
                }
                // Gone from what it owns first, and from the count of
                // sessions last, so a session no longer counted has let go
                // of everything.
                _owned.Remove(session);
                RemoveHoldsLocked(session);
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
            List<AutomationSession> lapse = null;
            List<(AutomationSession, Owned)> runs = null;
            AutomationSession[] engineFollowers = null, profileFollowers = null;
            lock (_sync)
            {
                foreach (var session in _sessions)
                {
                    if (!_owned.TryGetValue(session, out var owned)) continue;
                    if (!session.Lapsed && now - session.LastHeardMs > session.LeaseMs
                        && (owned.Holds > 0 || Devices.OwnedBy(session).Count > 0))
                        (lapse ??= new()).Add(session);
                    if (owned.RunCount > 0 && now - owned.RunLastMs >= RunGapMs) (runs ??= new()).Add((session, owned));
                }
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
            if (lapse != null) foreach (var session in lapse) Lapse(session);
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

        /// <summary>The lease ran out: the holds are let go for good, the
        /// devices rest until the program drives them again, and the
        /// program hears <c>e lease lapsed</c>.</summary>
        internal void Lapse(AutomationSession session)
        {
            lock (_sync)
            {
                if (!_owned.ContainsKey(session)) return;
                session.Lapsed = true;
                if (RemoveHoldsLocked(session)) PublishHoldsLocked();
            }
            Devices.Rest(session);
            session.Outbox.Line("e lease lapsed");
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
            string word = on ? AutomationLevels.Word(level) : "off";
            lock (_sync)
            {
                foreach (var session in _sessions)
                {
                    if (!on && !(pluginsServed && session.IsPlugin)) close.Add(session);
                    session.Outbox.Line("e level " + word);
                    if ((!on || level < AutomationLevel.SendInputs) && !session.IsPlugin) devicesGo.Add(session);
                }
                if (!on || level < AutomationLevel.Read)
                {
                    foreach (var owned in _owned.Values)
                    {
                        owned.Subscriptions.Clear();
                        owned.Devices = owned.Profile = owned.Engine = false;
                    }
                    PublishWatchLocked();
                }
                if (!on || level < AutomationLevel.SendInputs)
                {
                    _holds.Clear();
                    foreach (var owned in _owned.Values) owned.Holds = 0;
                    PublishHoldsLocked();
                    // Under the lock a value is set in (Value), so a line
                    // that passed the old level's check before this cannot
                    // set its value after it.
                    ScriptValues.ReleaseAll();
                }
            }
            foreach (var session in devicesGo) Devices.SessionEnded(session, now: true);
            foreach (var session in close) session.CloseAfterWritten();
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
                case "own": Own(session, r); return;
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
            => (pluginToo && session.IsPlugin) || _host.Allows(needed);

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
                answer = _host.Commands.Answer(body, session.Stopping);
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
                lock (SettingsManager.UserDevices.SyncRoot) rows = AutomationDevices.Caps(ud);
            }
            else rows = AutomationDevices.Caps(Stand(entry));
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
                        PublishWatchLocked();
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
                Fail(session, r, AutomationAskKind.Subscribe, what, "bad-argument", "sub takes buttons, hats, axes or axis=");
                return;
            }

            lock (_sync)
            {
                if (!_owned.TryGetValue(session, out var owned)) return;
                // The level again, under the lock a drop below Read clears
                // what is followed in (OnLevel).
                if (!Allows(session, AutomationLevel.Read))
                {
                    Refuse(session, r, AutomationAskKind.Subscribe, what);
                    return;
                }
                owned.Subscriptions.RemoveAll(e => e.Device == device && e.What == made.What);
                if (owned.Subscriptions.Count >= MostSubscriptions)
                {
                    PublishWatchLocked();
                    Fail(session, r, AutomationAskKind.Subscribe, what, "too-many", "a session follows up to 256 things");
                    return;
                }
                owned.Subscriptions.Add(made);
                PublishWatchLocked();
            }
            Done(session, r, AutomationAskKind.Subscribe, what);
        }

        private void Unsubscribe(AutomationSession session, AutomationRequest r)
        {
            var bare = r.Bare();
            string what = string.Join(" ", bare);
            if (!Allows(session, AutomationLevel.Read)) { Refuse(session, r, AutomationAskKind.Unsubscribe, what); return; }
            string axis = r.Get("axis");
            lock (_sync)
            {
                if (!_owned.TryGetValue(session, out var owned)) return;
                if (bare.Count == 1 && bare[0] == "all")
                {
                    owned.Subscriptions.Clear();
                    owned.Devices = owned.Profile = owned.Engine = false;
                }
                else if (bare.Count == 1 && bare[0] == "devices") owned.Devices = false;
                else if (bare.Count == 1 && bare[0] == "profile") owned.Profile = false;
                else if (bare.Count == 1 && bare[0] == "engine") owned.Engine = false;
                else if (bare.Count >= 1)
                {
                    string id = bare[0];
                    string kind = axis != null ? "axis=" + axis : bare.Count > 1 ? bare[1].ToLowerInvariant() : null;
                    owned.Subscriptions.RemoveAll(e => e.DeviceId == id && (kind == null || e.What == kind));
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
        private void PublishWatchLocked()
        {
            var entries = new List<AutomationWatch.Entry>();
            var followers = new List<AutomationSession>();
            foreach (var session in _sessions)
            {
                if (!_owned.TryGetValue(session, out var owned)) continue;
                entries.AddRange(owned.Subscriptions);
                if (owned.Devices) followers.Add(session);
            }
            Watch.Publish(entries.ToArray(), followers.ToArray());
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
            string bad = ReadChange(r, change, i => shape.Axes.Count == 0 ? PlainUnits.DefaultRest(i) : RestOrDefault(shape, i));
            if (bad != null) { Fail(session, r, AutomationAskKind.Set, id, "bad-argument", bad); return; }
            AfterTheLevelCheck?.Invoke();
            entry.Device.Apply(change);
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
        internal static string ReadChange(AutomationRequest r, ProgramDeviceChange change, Func<int, int> restOf = null)
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
                if (TryControl(key, out char kind, out int index))
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

        /// <summary><c>own &lt;id&gt; b3=1 a0=40000</c>: holds controls of
        /// any device at a value until let go. A control another session
        /// holds refuses the whole line with <c>err busy</c>.</summary>
        private void Own(AutomationSession session, AutomationRequest r)
        {
            string id = FirstBare(r);
            if (!Allows(session, AutomationLevel.SendInputs)) { Refuse(session, r, AutomationAskKind.Own, id); return; }
            AfterTheLevelCheck?.Invoke();
            var (ud, entry) = FindDevice(id);
            Guid device = ud?.InstanceGuid ?? entry?.Guid ?? Guid.Empty;
            if (device == Guid.Empty) { Fail(session, r, AutomationAskKind.Own, id, "unknown-device"); return; }
            var wanted = new List<((Guid, char, int) Key, int Value)>();
            int[] rests = null;
            foreach (var word in r.Words)
            {
                if (!word.IsPair) continue;
                string key = word.Key.ToLowerInvariant();
                // Raw or plain, as an s line takes them (#600).
                int v = 0;
                bool read = TryControl(key, out char kind, out int index);
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
                if (!read)
                {
                    Fail(session, r, AutomationAskKind.Own, id, "bad-argument",
                        "own takes b<i>=0|1, a<i>=0..65535 or a percent, h<i>=-1..35999, degrees or directions: " + word.Raw);
                    return;
                }
                wanted.Add(((device, kind, index), v));
            }
            if (wanted.Count == 0) { Fail(session, r, AutomationAskKind.Own, id, "bad-argument", "own names at least one control"); return; }
            string busy = null;
            lock (_sync)
            {
                if (!_owned.TryGetValue(session, out var owned)) return;
                // The level again, under the lock the level's drop clears the
                // holds in (OnLevel): a line that passed the check above
                // before a drop is refused here, never held after it.
                if (Allows(session, AutomationLevel.SendInputs))
                {
                    foreach (var (key, _) in wanted)
                    {
                        if (_holds.TryGetValue(key, out var held) && held.Owner != session)
                        {
                            busy = key.Item2 + key.Item3.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            break;
                        }
                    }
                    if (busy == null)
                    {
                        foreach (var (key, value) in wanted)
                        {
                            if (!_holds.ContainsKey(key)) owned.Holds++;
                            _holds[key] = (session, value);
                        }
                        PublishHoldsLocked();
                    }
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
            lock (_sync)
            {
                if (!_owned.TryGetValue(session, out var owned)) return;
                if (bare.Count == 1 && bare[0] == "all")
                {
                    RemoveHoldsLocked(session);
                    PublishHoldsLocked();
                    Done(session, r, AutomationAskKind.Release, "all");
                    return;
                }
                var (ud, entry) = FindDevice(id);
                Guid device = ud?.InstanceGuid ?? entry?.Guid ?? Guid.Empty;
                if (device == Guid.Empty) { Fail(session, r, AutomationAskKind.Release, id, "unknown-device"); return; }
                bool all = bare.Count == 2 && bare[1] == "all";
                var keys = new List<(Guid, char, int)>();
                if (all)
                {
                    foreach (var pair in _holds)
                        if (pair.Key.Device == device && pair.Value.Owner == session) keys.Add(pair.Key);
                }
                else
                {
                    for (int i = 1; i < bare.Count; i++)
                    {
                        if (!TryControl(bare[i].ToLowerInvariant(), out char kind, out int index))
                        {
                            Fail(session, r, AutomationAskKind.Release, id, "bad-argument", "rel takes b<i>, a<i>, h<i> or all: " + bare[i]);
                            return;
                        }
                        keys.Add((device, kind, index));
                    }
                    if (keys.Count == 0) { Fail(session, r, AutomationAskKind.Release, id, "bad-argument", "rel names a control or all"); return; }
                }
                foreach (var key in keys)
                {
                    if (_holds.TryGetValue(key, out var held) && held.Owner == session)
                    {
                        _holds.Remove(key);
                        owned.Holds--;
                    }
                }
                PublishHoldsLocked();
            }
            Done(session, r, AutomationAskKind.Release, id);
        }

        /// <summary>Under the lock. True when the session held
        /// anything.</summary>
        private bool RemoveHoldsLocked(AutomationSession session)
        {
            var mine = new List<(Guid, char, int)>();
            foreach (var pair in _holds) if (pair.Value.Owner == session) mine.Add(pair.Key);
            foreach (var key in mine) _holds.Remove(key);
            if (_owned.TryGetValue(session, out var owned)) owned.Holds = 0;
            if (mine.Count > 0) PublishHoldsLocked();
            return mine.Count > 0;
        }

        /// <summary>Under the lock: every hold, put in force where Step 2
        /// reads it.</summary>
        private void PublishHoldsLocked()
        {
            var byDevice = new Dictionary<Guid, (List<(int, bool)> B, List<(int, int)> A, List<(int, int)> H)>();
            foreach (var pair in _holds)
            {
                var (device, kind, index) = pair.Key;
                if (!byDevice.TryGetValue(device, out var lists))
                    byDevice[device] = lists = (new List<(int, bool)>(), new List<(int, int)>(), new List<(int, int)>());
                int value = pair.Value.Value;
                if (kind == 'b') lists.B.Add((index, value == 1));
                else if (kind == 'a') lists.A.Add((index, value));
                else lists.H.Add((index, value));
            }
            var table = new Dictionary<Guid, ControlHolds.DeviceHolds>();
            foreach (var (device, lists) in byDevice)
                table[device] = new ControlHolds.DeviceHolds(lists.B.ToArray(), lists.A.ToArray(), lists.H.ToArray());
            ControlHolds.Publish(table);
        }

        /// <summary>How many controls a session holds, for tests.</summary>
        internal int HoldsOf(AutomationSession session)
        {
            lock (_sync) return _owned.TryGetValue(session, out var owned) ? owned.Holds : 0;
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

        /// <summary>Closes every session, as the pipe stops.</summary>
        internal void CloseAll()
        {
            foreach (var session in Snapshot()) session.Close("stopped");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            CloseAll();
            _clock.Dispose();
            Watch.Unhook();
            lock (_sync)
            {
                _holds.Clear();
                ControlHolds.Publish(null);
            }
            Devices.Dispose();
        }
    }
}
