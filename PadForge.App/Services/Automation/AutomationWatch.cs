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

            public abstract void Pass(CustomInputState s, long now);
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

            public Axes(AutomationSession session, Guid device, string id, int hz) : base(session, device, id)
            {
                Hz = hz;
                _period = Stopwatch.Frequency / hz;
                Slots = new AxisStreamSlots(id);
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
        /// hysteresis: it is above once it reaches <c>above</c>, and below
        /// again once it falls under <c>above - hyst</c>.</summary>
        internal sealed class Crossing : Entry
        {
            private bool _isAbove;

            public Crossing(AutomationSession session, Guid device, string id, int axis, int above, int hyst)
                : base(session, device, id)
            {
                Axis = axis;
                Above = above;
                Hysteresis = hyst;
            }

            public int Axis { get; }
            public int Above { get; }
            public int Hysteresis { get; }
            public override string What => "axis=" + Axis;

            public override void Pass(CustomInputState s, long now)
            {
                int value = Axis < CustomInputState.MaxAxis ? s.Axis[Axis] : s.Sliders[Axis - CustomInputState.MaxAxis];
                if (!Primed)
                {
                    _isAbove = value >= Above;
                    Primed = true;
                    return;
                }
                if (!_isAbove && value >= Above)
                {
                    _isAbove = true;
                    Session.Outbox.Crossing(DeviceId, Axis, true);
                }
                else if (_isAbove && value < Above - Hysteresis)
                {
                    _isAbove = false;
                    Session.Outbox.Crossing(DeviceId, Axis, false);
                }
            }
        }

        private sealed class Table
        {
            public Entry[] Entries = Array.Empty<Entry>();
            public AutomationSession[] DeviceFollowers = Array.Empty<AutomationSession>();
        }

        private Table _table = new Table();
        private readonly Dictionary<Guid, UserDevice> _byGuid = new(64);
        private HashSet<Guid> _online = new(), _next = new();
        private bool _onlinePrimed;
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
        /// sessions' lock.
        /// </summary>
        internal void Publish(Entry[] entries, AutomationSession[] deviceFollowers)
        {
            var table = new Table
            {
                Entries = entries ?? Array.Empty<Entry>(),
                DeviceFollowers = deviceFollowers ?? Array.Empty<AutomationSession>(),
            };
            Volatile.Write(ref _table, table);
            if (table.Entries.Length > 0 || table.DeviceFollowers.Length > 0)
                InputManager.AutomationPass = _hook;
            else if (ReferenceEquals(InputManager.AutomationPass, _hook))
                InputManager.AutomationPass = null;
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
            FollowOnline(devices, count, followers);
        }

        /// <summary>Connects and disconnects, for the sessions that follow
        /// devices. Nothing is made unless the set of online devices
        /// changed.</summary>
        private void FollowOnline(UserDevice[] devices, int count, AutomationSession[] followers)
        {
            int online = 0;
            bool changed = false;
            for (int i = 0; i < count; i++)
            {
                var ud = devices[i];
                if (ud == null || !ud.IsOnline) continue;
                online++;
                if (!_online.Contains(ud.InstanceGuid)) changed = true;
            }
            if (!changed && online == _online.Count && _onlinePrimed) return;

            _next.Clear();
            for (int i = 0; i < count; i++)
            {
                var ud = devices[i];
                if (ud != null && ud.IsOnline) _next.Add(ud.InstanceGuid);
            }
            if (_onlinePrimed)
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
        /// again from the next pass. Called by the sessions, never by the
        /// poll thread.</summary>
        internal void EngineStopped()
        {
            var table = Volatile.Read(ref _table);
            var followers = table.DeviceFollowers;
            var online = _online;
            _online = new HashSet<Guid>();
            _onlinePrimed = false;
            foreach (var g in online) Tell(followers, "e dev " + AutomationDevices.IdOf(g) + " offline");
            foreach (var entry in table.Entries) entry.Primed = false;
        }
    }
}
