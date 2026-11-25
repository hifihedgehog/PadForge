using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// What programs follow on devices (#597), and the poll thread's side of
    /// it. After Step 2 republishes the devices' states on each pass,
    /// <see cref="OnPass"/> compares what sessions watch against the last
    /// pass and queues event lines to each session's writer. It runs only
    /// while some session watches something, allocates nothing when nothing
    /// changed, and never waits on a program.
    ///
    /// <para>It reads each device's own state
    /// (<see cref="UserDevice.ReadInputState"/>), so a program that holds a
    /// button of a device still hears the button itself.</para>
    /// </summary>
    internal sealed class AutomationWatch
    {
        /// <summary>One thing a session follows on one device. Its fields
        /// past the constructor are the poll thread's.</summary>
        internal abstract class Entry
        {
            protected Entry(AutomationSession session, Guid device, string deviceId)
            {
                Session = session;
                Device = device;
                DeviceId = deviceId;
            }

            public readonly AutomationSession Session;
            public readonly Guid Device;
            public readonly string DeviceId;

            /// <summary>Whether the entry holds a pass to compare against.
            /// The first pass, and the first after the device comes back,
            /// only takes the state.</summary>
            public bool Primed;

            /// <summary>What a <c>sub</c> line names, for <c>unsub</c>.</summary>
            public abstract string What { get; }

            /// <summary>The controls the subscription takes from games while
            /// it lives (#600 item 23): each key as ControlHolds takes it,
            /// and the claim's own value. Empty for none.</summary>
            public List<(char Kind, int Index, object Value)> Claims { get; } = new();

            public abstract void Pass(CustomInputState s, long now);

            /// <summary>The device is missing from a pass that had it
            /// before: offline, or no longer read.</summary>
            public virtual void Gone() { }
        }

        internal sealed class Buttons : Entry
        {
            private readonly bool[] _last = new bool[CustomInputState.MaxButtons];

            public Buttons(AutomationSession session, Guid device, string id) : base(session, device, id) { }

            public override string What => "buttons";

            public override void Pass(CustomInputState s, long now)
            {
                var buttons = s.Buttons;
                if (!Primed)
                {
                    Array.Copy(buttons, _last, CustomInputState.MaxButtons);
                    Primed = true;
                    return;
                }
                var outbox = Session.Outbox;
                for (int i = 0; i < CustomInputState.MaxButtons; i++)
                {
                    bool down = buttons[i];
                    if (down == _last[i]) continue;
                    _last[i] = down;
                    outbox.Button(DeviceId, i, down);
                }
            }
        }

        internal sealed class Hats : Entry
        {
            private readonly int[] _last = new int[CustomInputState.MaxPovs];

            public Hats(AutomationSession session, Guid device, string id) : base(session, device, id) { }

            public override string What => "hats";

            public override void Pass(CustomInputState s, long now)
            {
                if (!Primed)
                {
                    Array.Copy(s.Povs, _last, CustomInputState.MaxPovs);
                    Primed = true;
                    return;
                }
                for (int i = 0; i < CustomInputState.MaxPovs; i++)
                {
                    int value = s.Povs[i];
                    if (value == _last[i]) continue;
                    _last[i] = value;
                    Session.Outbox.Hat(DeviceId, i, value);
                }
            }
        }

        /// <summary>Every axis and slider at most <c>hz</c> times a second.
        /// The writer keeps only the newest value of each while a write is
        /// pending.</summary>
        internal sealed class Axes : Entry
        {
            private readonly int[] _last = new int[ProgramAxis.End];
            private readonly long _period;
            private long _due;

            public Axes(AutomationSession session, Guid device, string id, int hz, int[] rests = null) : base(session, device, id)
            {
                Hz = hz;
                _period = Stopwatch.Frequency / hz;
                Slots = new AxisStreamSlots(id, rests);
            }

            public int Hz { get; }
            public readonly AxisStreamSlots Slots;
            public override string What => "axes";

            private static int Value(CustomInputState s, int i)
                => i < CustomInputState.MaxAxis ? s.Axis[i] : s.Sliders[i - CustomInputState.MaxAxis];

            public override void Pass(CustomInputState s, long now)
            {
                if (!Primed)
                {
                    for (int i = 0; i < ProgramAxis.End; i++) _last[i] = Value(s, i);
                    Primed = true;
                    _due = now;
                    return;
                }
                if (now < _due) return;
                bool sent = false;
                for (int i = 0; i < ProgramAxis.End; i++)
                {
                    int value = Value(s, i);
                    if (value == _last[i]) continue;
                    _last[i] = value;
                    Session.Outbox.Axis(Slots, i, value);
                    sent = true;
                }
                // The rate counts from a change sent, so the first change
                // after a quiet spell goes out on its own pass.
                if (sent) _due = now + _period;
            }
        }

        /// <summary>An axis crossing a line, worked out here with
        /// hysteresis. On the high side (<c>above=</c>) it is past once it
        /// reaches the line, and back once it falls under the line less the
        /// band. On the low side (<c>below=</c>, #600 item 23) it is past
        /// once it reaches the line from above, and back once it rises over
        /// the line and the band. A line of a named subscription has a
        /// number, which its events repeat (<c>e x &lt;id&gt; &lt;axis&gt; 1
        /// &lt;line&gt;</c>), and tells the whole state first.</summary>
        internal sealed class Crossing : Entry
        {
            private bool _past;
            private readonly StateOnce _state;

            public Crossing(AutomationSession session, Guid device, string id, int axis, int above, int hyst,
                bool low = false, int line = 0, int[] rests = null, int buttons = 0)
                : base(session, device, id)
            {
                Axis = axis;
                Above = above;
                Hysteresis = hyst;
                Low = low;
                Line = line;
                _state = line > 0 ? new StateOnce(id, rests, buttons) : null;
            }

            public int Axis { get; }

            /// <summary>The line, raw.</summary>
            public int Above { get; }
            public int Hysteresis { get; }

            /// <summary>The line is on the low side.</summary>
            public bool Low { get; }

            /// <summary>The line's number, or 0 for a line of the
            /// <c>axis=</c> form, whose events carry none.</summary>
            public int Line { get; }
            public override string What => Line > 0 ? "line=" + Line : "axis=" + Axis;

            private bool IsPast(int value) => Low ? value <= Above : value >= Above;
            private bool IsBack(int value) => Low ? value > Above + Hysteresis : value < Above - Hysteresis;

            public override void Pass(CustomInputState s, long now)
            {
                int value = Axis < CustomInputState.MaxAxis ? s.Axis[Axis] : s.Sliders[Axis - CustomInputState.MaxAxis];
                if (!Primed)
                {
                    _state?.Send(Session, s);
                    _past = IsPast(value);
                    Primed = true;
                    return;
                }
                if (!_past && IsPast(value))
                {
                    _past = true;
                    Session.Outbox.Crossing(DeviceId, Axis, true, Line);
                }
                else if (_past && IsBack(value))
                {
                    _past = false;
                    Session.Outbox.Crossing(DeviceId, Axis, false, Line);
                }
            }

            public override void Gone() => _state?.Gone(Session);
        }

        /// <summary>The whole state of a device, told when a named
        /// subscription starts and each time the device comes back
        /// (<c>e state &lt;id&gt; b=... a=... h=...</c>, plain for a plain
        /// session), and <c>e state &lt;id&gt; offline</c> when it goes
        /// (#600 item 21). Made on the poll thread at those times
        /// only.</summary>
        internal sealed class StateOnce
        {
            private readonly string _id;
            private readonly int[] _rests;
            private readonly int _buttons;

            public StateOnce(string id, int[] rests, int buttons)
            {
                _id = id;
                _rests = rests ?? AxisStreamSlots.DefaultRests();
                _buttons = buttons <= 0 ? CustomInputState.MaxButtons : buttons;
            }

            public void Send(AutomationSession session, CustomInputState s)
            {
                string line = session.Outbox.Plain
                    ? AutomationDevices.StateLine(s, _buttons, _rests)
                    : AutomationDevices.StateLine(s, _buttons);
                // StateLine opens with "ok ".
                session.Outbox.Line("e state " + _id + " " + line.Substring(3));
            }

            public void Gone(AutomationSession session) => session.Outbox.Line("e state " + _id + " offline");
        }

        /// <summary>A device's whole state (<c>sub &lt;id&gt; state</c>) or
        /// one named control (<c>sub &lt;id&gt; A</c>): the whole state first,
        /// then every change of what it follows, axes at most <c>hz</c>
        /// times a second (#600 item 21).</summary>
        internal sealed class State : Entry
        {
            private readonly bool[] _buttons = new bool[CustomInputState.MaxButtons];
            private readonly int[] _hats = new int[CustomInputState.MaxPovs];
            private readonly int[] _axes = new int[ProgramAxis.End];
            private readonly StateOnce _state;
            private readonly long _period;
            private readonly string _name;
            private long _due;

            /// <param name="only">One control to follow, (kind, index), or
            /// null for every control.</param>
            /// <param name="name">That control's name, for unsub.</param>
            public State(AutomationSession session, Guid device, string id, int hz, int[] rests, int buttons,
                (char Kind, int Index)? only = null, string name = null) : base(session, device, id)
            {
                Only = only;
                _name = name;
                _period = Stopwatch.Frequency / Math.Clamp(hz, 1, 1000);
                _state = new StateOnce(id, rests, buttons);
                Slots = new AxisStreamSlots(id, rests);
            }

            public (char Kind, int Index)? Only { get; }
            public readonly AxisStreamSlots Slots;
            public override string What => _name == null ? "state" : _name.ToLowerInvariant();

            private static int Value(CustomInputState s, int i)
                => i < CustomInputState.MaxAxis ? s.Axis[i] : s.Sliders[i - CustomInputState.MaxAxis];

            public override void Pass(CustomInputState s, long now)
            {
                if (!Primed)
                {
                    Array.Copy(s.Buttons, _buttons, CustomInputState.MaxButtons);
                    Array.Copy(s.Povs, _hats, CustomInputState.MaxPovs);
                    for (int i = 0; i < ProgramAxis.End; i++) _axes[i] = Value(s, i);
                    _state.Send(Session, s);
                    Primed = true;
                    _due = now;
                    return;
                }
                var outbox = Session.Outbox;
                var only = Only;
                if (only == null || only.Value.Kind == 'b')
                {
                    int from = only?.Index ?? 0, to = only == null ? CustomInputState.MaxButtons : only.Value.Index + 1;
                    for (int i = from; i < to; i++)
                    {
                        bool down = s.Buttons[i];
                        if (down == _buttons[i]) continue;
                        _buttons[i] = down;
                        outbox.Button(DeviceId, i, down);
                    }
                }
                if (only == null || only.Value.Kind == 'h')
                {
                    int from = only?.Index ?? 0, to = only == null ? CustomInputState.MaxPovs : only.Value.Index + 1;
                    for (int i = from; i < to; i++)
                    {
                        int value = s.Povs[i];
                        if (value == _hats[i]) continue;
                        _hats[i] = value;
                        outbox.Hat(DeviceId, i, value);
                    }
                }
                if ((only == null || only.Value.Kind == 'a') && now >= _due)
                {
                    int from = only?.Index ?? 0, to = only == null ? ProgramAxis.End : only.Value.Index + 1;
                    bool sent = false;
                    for (int i = from; i < to; i++)
                    {
                        int value = Value(s, i);
                        if (value == _axes[i]) continue;
                        _axes[i] = value;
                        outbox.Axis(Slots, i, value);
                        sent = true;
                    }
                    // The rate counts from a change sent, so the first
                    // change after a quiet spell goes out on its own pass.
                    if (sent) _due = now + _period;
                }
            }

            public override void Gone() => _state.Gone(Session);
        }

        private sealed class Table
        {
            public Entry[] Entries = Array.Empty<Entry>();
            public AutomationSession[] DeviceFollowers = Array.Empty<AutomationSession>();
            public Baseline Online;
        }

        /// <summary>The devices online as the first program came to follow
        /// devices. The first pass after tells what changed since, so a
        /// device that came or went in between is not lost.</summary>
        internal sealed class Baseline
        {
            public UserDevice[] Devices = Array.Empty<UserDevice>();
            public readonly HashSet<Guid> Ids = new();
        }

        private Table _table = new Table();
        private readonly Dictionary<Guid, UserDevice> _byGuid = new(64);
        private HashSet<Guid> _online = new(), _next = new();
        private bool _onlinePrimed;
        private Baseline _baselineTold;
        private readonly Action<UserDevice[], int> _hook;

        public AutomationWatch()
        {
            _hook = OnPass;
        }

        /// <summary>Whether the engine's pass hook is this watch's: set when
        /// something is followed, cleared when nothing is.</summary>
        internal bool Hooked => ReferenceEquals(InputManager.AutomationPass, _hook);

        /// <summary>
        /// Puts what sessions follow in force. The entries are the same
        /// objects from one table to the next while their subscription
        /// lasts, so they keep what they last saw. Called under the
        /// sessions' lock. <paramref name="arriving"/> is the devices online
        /// as a session came to follow devices: kept from the first such
        /// session until none follows them.
        /// </summary>
        internal void Publish(Entry[] entries, AutomationSession[] deviceFollowers, Baseline arriving = null)
        {
            var before = Volatile.Read(ref _table);
            var table = new Table
            {
                Entries = entries ?? Array.Empty<Entry>(),
                DeviceFollowers = deviceFollowers ?? Array.Empty<AutomationSession>(),
            };
            if (table.DeviceFollowers.Length > 0)
                table.Online = before.DeviceFollowers.Length > 0 ? before.Online : arriving;
            Volatile.Write(ref _table, table);
            if (table.Entries.Length > 0 || table.DeviceFollowers.Length > 0)
                InputManager.AutomationPass = _hook;
            else if (ReferenceEquals(InputManager.AutomationPass, _hook))
                InputManager.AutomationPass = null;
        }

        /// <summary>The devices online now, read under the device list's
        /// lock. Called outside the sessions' lock.</summary>
        internal static Baseline OnlineNow()
        {
            var baseline = new Baseline();
            var devices = SettingsManager.UserDevices;
            if (devices == null) return baseline;
            var online = new List<UserDevice>();
            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                    if (ud != null && ud.IsOnline && baseline.Ids.Add(ud.InstanceGuid)) online.Add(ud);
            }
            baseline.Devices = online.ToArray();
            return baseline;
        }

        /// <summary>Lets go of the engine's hook for good.</summary>
        internal void Unhook()
        {
            if (ReferenceEquals(InputManager.AutomationPass, _hook)) InputManager.AutomationPass = null;
        }

        /// <summary>
        /// The poll thread, at the end of Step 2: the devices read on this
        /// pass and their count.
        /// </summary>
        internal void OnPass(UserDevice[] devices, int count)
        {
            var table = Volatile.Read(ref _table);
            var entries = table.Entries;
            long now = Stopwatch.GetTimestamp();

            if (entries.Length > 0)
            {
                _byGuid.Clear();
                for (int i = 0; i < count; i++)
                {
                    var ud = devices[i];
                    if (ud != null && ud.IsOnline) _byGuid[ud.InstanceGuid] = ud;
                }
                for (int i = 0; i < entries.Length; i++)
                {
                    var entry = entries[i];
                    if (!_byGuid.TryGetValue(entry.Device, out var ud))
                    {
                        if (entry.Primed) entry.Gone();
                        entry.Primed = false;
                        continue;
                    }
                    var state = ud.ReadInputState ?? ud.InputState;
                    if (state == null) continue;
                    entry.Pass(state, now);
                }
            }

            var followers = table.DeviceFollowers;
            if (followers.Length == 0)
            {
                _onlinePrimed = false;
                return;
            }
            FollowOnline(devices, count, followers, table.Online);
        }

        /// <summary>Connects and disconnects, for the sessions that follow
        /// devices. Nothing is made unless the set of online devices
        /// changed, or the followers' baseline is not told yet.</summary>
        private void FollowOnline(UserDevice[] devices, int count, AutomationSession[] followers, Baseline arrived)
        {
            // The first look since the first of these followers came: what
            // they know is the baseline, whatever the last followers were
            // told.
            bool first = arrived != null && !ReferenceEquals(arrived, _baselineTold);
            int online = 0;
            bool changed = false;
            for (int i = 0; i < count; i++)
            {
                var ud = devices[i];
                if (ud == null || !ud.IsOnline) continue;
                online++;
                if (!_online.Contains(ud.InstanceGuid)) changed = true;
            }
            if (!changed && online == _online.Count && _onlinePrimed && !first) return;

            _next.Clear();
            for (int i = 0; i < count; i++)
            {
                var ud = devices[i];
                if (ud != null && ud.IsOnline) _next.Add(ud.InstanceGuid);
            }
            if (first)
            {
                // A device online then that is not now went, and one online
                // now that was not then came.
                _baselineTold = arrived;
                foreach (var ud in arrived.Devices)
                    if (!ud.IsOnline) Tell(followers, "e dev " + AutomationDevices.IdOf(ud.InstanceGuid) + " offline");
                foreach (var g in _next)
                    if (!arrived.Ids.Contains(g)) Tell(followers, "e dev " + AutomationDevices.IdOf(g) + " online");
            }
            else if (_onlinePrimed)
            {
                foreach (var g in _next)
                    if (!_online.Contains(g)) Tell(followers, "e dev " + AutomationDevices.IdOf(g) + " online");
                foreach (var g in _online)
                    if (!_next.Contains(g)) Tell(followers, "e dev " + AutomationDevices.IdOf(g) + " offline");
            }
            (_online, _next) = (_next, _online);
            _onlinePrimed = true;
        }

        private static void Tell(AutomationSession[] followers, string line)
        {
            foreach (var session in followers) session.Outbox.Line(line);
        }

        /// <summary>The engine stopped: no pass runs, so the devices a
        /// follower saw online are told offline here, and every entry starts
        /// again from the next pass. The followers then know of no device
        /// online, so the first pass of the next start tells each device
        /// online then as come back (#600). Called by the sessions, never by
        /// the poll thread.</summary>
        internal void EngineStopped()
        {
            var table = Volatile.Read(ref _table);
            var followers = table.DeviceFollowers;
            // What the followers saw online: as of the last pass, or as they
            // came if no pass has looked since.
            var seen = table.Online != null && !ReferenceEquals(table.Online, _baselineTold)
                ? table.Online.Ids
                : _online;
            if (table.Online != null) _baselineTold = table.Online;
            _online = new HashSet<Guid>();
            _onlinePrimed = followers.Length > 0;
            foreach (var g in seen) Tell(followers, "e dev " + AutomationDevices.IdOf(g) + " offline");
            foreach (var entry in table.Entries)
            {
                if (entry.Primed) entry.Gone();
                entry.Primed = false;
            }
        }
    }
}
