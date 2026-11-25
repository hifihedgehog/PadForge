using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Automation (#597): devices programs add, and the pass hook
        // ─────────────────────────────────────────────

        /// <summary>
        /// Called on the poll thread at the end of Step 2 on every pass, the
        /// idle ones included, with the devices read on it and their count.
        /// Automation's watcher queues events from it and returns: it never
        /// waits on a program. Null while no program follows anything, so a
        /// pass with no follower pays one read of this field.
        /// </summary>
        internal static volatile Action<UserDevice[], int> AutomationPass;

        /// <summary>
        /// Registers a device a program added, by push from the program's
        /// connection, as a web pad is (RegisterExternalDevice). A device
        /// already online is reloaded in place, so a declaration that
        /// changes its shape reaches the row, its pickers and the Devices
        /// page with its mappings kept. Thread-safe through
        /// UserDevices.SyncRoot.
        /// </summary>
        public void RegisterProgramDevice(ProgramDevice device)
        {
            if (device == null) return;

            UserDevice ud = FindOrCreateUserDevice(device.InstanceGuid, device.ProductGuid);
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                Load(ud, device);
                ud.IsOnline = true;
            }

            device.CapabilitiesChanged -= OnProgramDeviceCapabilitiesChanged;
            device.CapabilitiesChanged += OnProgramDeviceCapabilitiesChanged;

            DevicesUpdated?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Loads a program device's row, which keeps the device's
        /// own product name when it has one (ProgramDevice.ProductName). The
        /// caller holds the device list's lock.</summary>
        private static void Load(UserDevice ud, ProgramDevice device)
        {
            ud.LoadFromExternalDevice(device);
            string product = device.ProductName;
            if (!string.IsNullOrEmpty(product)) ud.ProductName = product;
        }

        /// <summary>Takes a program's device offline with its mapped outputs
        /// at rest, through the path a web pad takes. The row stays, as any
        /// disconnected device's does.</summary>
        public void UnregisterProgramDevice(ProgramDevice device)
        {
            if (device == null) return;
            device.CapabilitiesChanged -= OnProgramDeviceCapabilitiesChanged;
            UnregisterExternalDevice(device.InstanceGuid);
        }

        /// <summary>A program declared a device again with a new shape. The
        /// rows of program devices read their shape again, as the web pads'
        /// rows do when a phone's capabilities change.</summary>
        private void OnProgramDeviceCapabilitiesChanged()
        {
            try
            {
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    foreach (var ud in SettingsManager.UserDevices.Items)
                        if (ud?.Device is ProgramDevice program && ud.IsOnline)
                            Load(ud, program);
                }
            }
            catch (InvalidOperationException)
            {
                // A row list that changed under the walk. The next
                // declaration or connect reloads it.
            }
            DevicesUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The values programs hold controls of devices at (#597), and the
    /// controls their hotkeys take from games (#600 item 23), as the poll
    /// thread reads them. A program's <c>own</c> line puts a value over a
    /// button, an axis, a slider or a hat of any device, a <c>set</c> line
    /// puts one there until the device's own input moves, and a claim takes
    /// a button, a hat's direction or an axis past a line away from what
    /// mappings read. Step 2 applies them where the On-Screen Keyboard filter
    /// swaps what mappings read. The device's own state stays in
    /// <see cref="UserDevice.ReadInputState"/>, so a program that holds or
    /// claims a button still hears it pressed.
    ///
    /// <para>Programs send steps, each with the clock tick it is due at. At
    /// the start of every pass (<see cref="BeginPass"/>) the poll thread
    /// takes the steps that are due in the order they came, one change per
    /// control per pass: a step whose control already changed on this pass,
    /// or that is not due, holds back every later step of the same control
    /// to a later pass. So a press and its release sent between two passes
    /// land on two passes, and a timed sequence plays on PadForge's clock in
    /// its order. With no pass running (the engine stopped), a step lands
    /// when it is sent or, timed, at the sessions' clock
    /// (<see cref="LandDue"/>).</para>
    ///
    /// <para>The table mappings read is rebuilt only when a step lands, and
    /// is read on each device with one read and no lock.</para>
    /// </summary>
    internal static class ControlHolds
    {
        /// <summary>A held value, one object for the life of the hold. A
        /// <c>set</c> hold (<see cref="UntilMoved"/>) is let go by the pass
        /// once the device's own value of the control differs from what it
        /// was when the hold landed: the poll thread marks it
        /// <see cref="Moved"/> and the sessions take it away.</summary>
        internal sealed class Hold
        {
            public Hold(int value, bool untilMoved = false)
            {
                Value = value;
                UntilMoved = untilMoved;
            }

            /// <summary>A button 0 or 1, an axis or slider raw, a hat in
            /// hundredths or -1.</summary>
            public readonly int Value;
            public readonly bool UntilMoved;

            // The poll thread's: the device's own value when the hold
            // landed.
            internal int Baseline = int.MinValue;
            private volatile bool _moved;

            /// <summary>The device's own input moved the control since the
            /// hold landed.</summary>
            public bool Moved
            {
                get => _moved;
                internal set => _moved = value;
            }
        }

        /// <summary>An axis or slider taken from the game while it is past
        /// its line: it reads at its rest then.</summary>
        internal sealed class Line
        {
            public Line(int axis, int raw, bool above, int rest)
            {
                Axis = axis;
                Raw = raw;
                Above = above;
                Rest = rest;
            }

            public readonly int Axis, Raw, Rest;
            public readonly bool Above;

            public bool Past(int value) => Above ? value >= Raw : value <= Raw;
        }

        /// <summary>The kinds a step's key names: a held button, axis or
        /// hat, a claimed button, a claimed hat direction (index = hat * 4 +
        /// 0 for Up, 1 Right, 2 Down, 3 Left), and a claimed line (index =
        /// the line's own number).</summary>
        internal const char HoldButton = 'b', HoldAxis = 'a', HoldHat = 'h', ClaimButton = 'B', ClaimDirection = 'D', ClaimLine = 'L';

        private readonly struct Step
        {
            public Step(int owner, Guid device, char kind, int index, object value, long due, bool drop)
            {
                Owner = owner;
                Device = device;
                Kind = kind;
                Index = index;
                Value = value;
                Due = due;
                Drop = drop;
            }

            public readonly int Owner;
            public readonly Guid Device;
            public readonly char Kind;
            public readonly int Index;

            /// <summary>A <see cref="Hold"/>, a <see cref="Line"/>, true for
            /// a claimed button or direction, or null to let go.</summary>
            public readonly object Value;
            public readonly long Due;

            /// <summary>Lets go of everything of the owner at once.</summary>
            public readonly bool Drop;
        }

        /// <summary>The holds and claims on one device, for one table.
        /// Written once before it is published, apart from its pool and the
        /// set holds' marks, which only the poll thread touches.</summary>
        internal sealed class DeviceHolds
        {
            /// <summary>A table of plain holds, as a test or a recording's
            /// replay gives one.</summary>
            public DeviceHolds((int Index, bool Down)[] buttons, (int Index, int Value)[] axes, (int Index, int Value)[] hats)
            {
                var b = new List<(int, Hold)>();
                if (buttons != null) foreach (var (i, down) in buttons) b.Add((i, new Hold(down ? 1 : 0)));
                var a = new List<(int, Hold)>();
                if (axes != null) foreach (var (i, v) in axes) a.Add((i, new Hold(v)));
                var h = new List<(int, Hold)>();
                if (hats != null) foreach (var (i, v) in hats) h.Add((i, new Hold(v)));
                Buttons = b.ToArray();
                Axes = a.ToArray();
                Hats = h.ToArray();
            }

            internal DeviceHolds((int, Hold)[] buttons, (int, Hold)[] axes, (int, Hold)[] hats,
                int[] claimedButtons, (int Hat, HatDirections Directions)[] claimedDirections, Line[] lines)
            {
                Buttons = buttons;
                Axes = axes;
                Hats = hats;
                ClaimedButtons = claimedButtons;
                ClaimedDirections = claimedDirections;
                Lines = lines;
            }

            /// <summary>Buttons by index, below 256.</summary>
            public readonly (int Index, Hold Hold)[] Buttons;

            /// <summary>Axes 0 to 23 and sliders 24 to 31, in
            /// <see cref="CustomInputState"/>'s range.</summary>
            public readonly (int Index, Hold Hold)[] Axes;

            /// <summary>Hats 0 to 3, in hundredths of a degree or -1.</summary>
            public readonly (int Index, Hold Hold)[] Hats;

            public readonly int[] ClaimedButtons = Array.Empty<int>();
            public readonly (int Hat, HatDirections Directions)[] ClaimedDirections = Array.Empty<(int, HatDirections)>();
            public readonly Line[] Lines = Array.Empty<Line>();

            private PooledInputStatePair _pool;

            private static int AxisOf(CustomInputState s, int index)
                => index < CustomInputState.MaxAxis ? s.Axis[index] : s.Sliders[index - CustomInputState.MaxAxis];

            private static void SetAxis(CustomInputState s, int index, int value)
            {
                if (index < CustomInputState.MaxAxis) s.Axis[index] = value;
                else if (index - CustomInputState.MaxAxis < CustomInputState.MaxSliders) s.Sliders[index - CustomInputState.MaxAxis] = value;
            }

            /// <summary>Whether a hold is in force on this read: a set hold
            /// lets go once the device's own value moved.</summary>
            private static bool InForce(Hold hold, int own)
            {
                if (!hold.UntilMoved) return true;
                if (hold.Moved) return false;
                if (hold.Baseline == int.MinValue)
                {
                    hold.Baseline = own;
                    return true;
                }
                if (own == hold.Baseline) return true;
                hold.Moved = true;
                return false;
            }

            /// <summary>The state mappings read: the device's own with the
            /// claims taken out and the held controls over it, in a buffer
            /// of this table's own.</summary>
            public CustomInputState Apply(CustomInputState read)
            {
                var s = _pool.Next();
                read.CopyInto(s);
                foreach (int index in ClaimedButtons)
                    if ((uint)index < (uint)s.Buttons.Length) s.Buttons[index] = false;
                foreach (var (hat, directions) in ClaimedDirections)
                {
                    if ((uint)hat >= (uint)s.Povs.Length) continue;
                    var held = PlainUnits.HatToDirections(s.Povs[hat]);
                    if ((held & directions) != 0) s.Povs[hat] = PlainUnits.DirectionsToHat(held & ~directions);
                }
                foreach (var line in Lines)
                    if ((uint)line.Axis < (uint)ProgramAxis.End && line.Past(AxisOf(read, line.Axis))) SetAxis(s, line.Axis, line.Rest);
                foreach (var (index, hold) in Buttons)
                    if ((uint)index < (uint)s.Buttons.Length && InForce(hold, read.Buttons[index] ? 1 : 0)) s.Buttons[index] = hold.Value != 0;
                foreach (var (index, hold) in Axes)
                    if ((uint)index < (uint)ProgramAxis.End && InForce(hold, AxisOf(read, index))) SetAxis(s, index, hold.Value);
                foreach (var (index, hold) in Hats)
                    if ((uint)index < (uint)s.Povs.Length && InForce(hold, read.Povs[index])) s.Povs[index] = hold.Value;
                return s;
            }
        }

        private static Dictionary<Guid, DeviceHolds> _current;

        // The steps sent and not yet taken by a pass, in their order.
        private static readonly ConcurrentQueue<Step> Sent = new();
        private static int _sentCount;

        // Under _passLock: steps taken that wait, and what each owner holds
        // and claims now.
        private static readonly object _passLock = new object();
        private static readonly List<Step> _waiting = new();
        private static readonly List<Step> _kept = new();
        private static readonly Dictionary<int, Dictionary<(Guid, char, int), object>> _owners = new();
        private static readonly HashSet<(Guid, char, int)> _changed = new();
        private static readonly HashSet<(Guid, char, int)> _blocked = new();
        private static long _lastPass;
        private static int _waitingCount;
        private static bool _dirty;

        // When a pass last read each device: a device no pass reads (the
        // engine stopped, or the device not on its list) has its steps land
        // when they are due, without waiting for a pass.
        private static readonly ConcurrentDictionary<Guid, long> _seen = new();

        /// <summary>The most steps that wait. Past it the oldest of them
        /// land at once, so a program that sends without end cannot grow
        /// the list.</summary>
        internal const int MostWaiting = 4096;

        /// <summary>Test seam: the clock, in Stopwatch ticks. Null reads
        /// Stopwatch.</summary>
        internal static Func<long> ClockForTest;

        internal static long Now() => ClockForTest?.Invoke() ?? Stopwatch.GetTimestamp();

        /// <summary>The table in force, or null when nothing is held or
        /// claimed.</summary>
        internal static IReadOnlyDictionary<Guid, DeviceHolds> Current => Volatile.Read(ref _current);

        /// <summary>Whether a step waits to land.</summary>
        internal static bool Pending => Volatile.Read(ref _sentCount) > 0 || Volatile.Read(ref _waitingCount) > 0;

        /// <summary>With null, lets go of every hold and claim at once and
        /// forgets the steps that wait. With a table, puts that table in
        /// force alone. Tests use it, and a host that goes.</summary>
        internal static void Publish(Dictionary<Guid, DeviceHolds> table)
        {
            lock (_passLock)
            {
                while (Sent.TryDequeue(out _)) { }
                Volatile.Write(ref _sentCount, 0);
                _waiting.Clear();
                Volatile.Write(ref _waitingCount, 0);
                _owners.Clear();
                _dirty = false;
                _seen.Clear();
                Volatile.Write(ref _current, table == null || table.Count == 0 ? null : table);
            }
        }

        /// <summary>Sends a step: <paramref name="value"/> is a
        /// <see cref="Hold"/> for a held button, axis or hat, true for a
        /// claimed button or direction, a <see cref="Line"/> for a claimed
        /// line, or null to let the control go. Due at
        /// <paramref name="due"/> (Stopwatch ticks, 0 for now).</summary>
        internal static void Send(int owner, Guid device, char kind, int index, object value, long due = 0)
        {
            Sent.Enqueue(new Step(owner, device, kind, index, value, due, drop: false));
            Interlocked.Increment(ref _sentCount);
            LandDue();
        }

        /// <summary>Lets go of everything an owner holds and claims, at once,
        /// and of its steps that wait.</summary>
        internal static void Drop(int owner)
        {
            Sent.Enqueue(new Step(owner, Guid.Empty, '\0', 0, null, 0, drop: true));
            Interlocked.Increment(ref _sentCount);
            LandDue();
        }

        /// <summary>What an owner holds and claims as the poll thread has
        /// it now, for tests.</summary>
        internal static int CountOf(int owner)
        {
            lock (_passLock) return _owners.TryGetValue(owner, out var held) ? held.Count : 0;
        }

        /// <summary>The steps of devices no pass reads land here when they
        /// are due: at a send, and at the sessions' clock for a timed
        /// step. A device a pass reads waits for the pass.</summary>
        internal static void LandDue()
        {
            long now = Now();
            lock (_passLock) Advance(now, inPass: false);
        }

        private static bool Polled(Guid device, long now)
            => _seen.TryGetValue(device, out long at) && now - at <= ProgramDevice.NotPolledTicks;

        /// <summary>The start of a pass, on the poll thread, before any
        /// device is read: the steps that are due land, one change per
        /// control. Costs one read when nothing waits.</summary>
        internal static void BeginPass()
        {
            long now = Now();
            Volatile.Write(ref _lastPass, now);
            if (!Pending) return;
            lock (_passLock) Advance(now, inPass: true);
        }

        /// <summary>Under the lock: takes what was sent and lands what is
        /// due, in order. In a pass (<paramref name="inPass"/>) each control
        /// changes once. Out of one, only the steps of devices no pass reads
        /// land. The table is rebuilt when anything landed.</summary>
        private static void Advance(long now, bool inPass)
        {
            while (Sent.TryDequeue(out var step))
            {
                Interlocked.Decrement(ref _sentCount);
                if (step.Drop)
                {
                    int owner = step.Owner;
                    _waiting.RemoveAll(s => s.Owner == owner);
                    if (_owners.Remove(owner)) _dirty = true;
                    continue;
                }
                _waiting.Add(step);
            }
            if (_waiting.Count > 0)
            {
                _changed.Clear();
                _blocked.Clear();
                _kept.Clear();
                int overflow = _waiting.Count - MostWaiting;
                for (int i = 0; i < _waiting.Count; i++)
                {
                    var step = _waiting[i];
                    var key = (step.Device, step.Kind, step.Index);
                    bool land = i < overflow
                        || (step.Due <= now && !_blocked.Contains(key)
                            && (inPass ? !_changed.Contains(key) : !Polled(step.Device, now)));
                    if (!land)
                    {
                        _kept.Add(step);
                        _blocked.Add(key);
                        continue;
                    }
                    if (!_owners.TryGetValue(step.Owner, out var held))
                        _owners[step.Owner] = held = new Dictionary<(Guid, char, int), object>();
                    if (step.Value == null) held.Remove(key);
                    else held[key] = step.Value;
                    if (held.Count == 0) _owners.Remove(step.Owner);
                    _changed.Add(key);
                    _dirty = true;
                }
                _waiting.Clear();
                _waiting.AddRange(_kept);
                _kept.Clear();
            }
            Volatile.Write(ref _waitingCount, _waiting.Count);
            if (_dirty) Rebuild();
        }

        /// <summary>Under the lock: the table mappings read, from what every
        /// owner holds and claims.</summary>
        private static void Rebuild()
        {
            _dirty = false;
            var byDevice = new Dictionary<Guid, Parts>();
            foreach (var held in _owners.Values)
            {
                foreach (var ((device, kind, index), value) in held)
                {
                    if (!byDevice.TryGetValue(device, out var parts)) byDevice[device] = parts = new Parts();
                    switch (kind)
                    {
                        case HoldButton: parts.Buttons.Add((index, (Hold)value)); break;
                        case HoldAxis: parts.Axes.Add((index, (Hold)value)); break;
                        case HoldHat: parts.Hats.Add((index, (Hold)value)); break;
                        case ClaimButton: parts.Claimed.Add(index); break;
                        case ClaimDirection:
                        {
                            int hat = index / 4;
                            parts.Directions.TryGetValue(hat, out var d);
                            parts.Directions[hat] = d | (HatDirections)(1 << (index % 4));
                            break;
                        }
                        case ClaimLine: parts.Lines.Add((Line)value); break;
                    }
                }
            }
            var table = new Dictionary<Guid, DeviceHolds>();
            foreach (var (device, parts) in byDevice)
            {
                var directions = new List<(int, HatDirections)>();
                foreach (var (hat, d) in parts.Directions) directions.Add((hat, d));
                table[device] = new DeviceHolds(parts.Buttons.ToArray(), parts.Axes.ToArray(), parts.Hats.ToArray(),
                    parts.Claimed.ToArray(), directions.ToArray(), parts.Lines.ToArray());
            }
            Volatile.Write(ref _current, table.Count == 0 ? null : table);
        }

        private sealed class Parts
        {
            public readonly List<(int, Hold)> Buttons = new(), Axes = new(), Hats = new();
            public readonly List<int> Claimed = new();
            public readonly Dictionary<int, HatDirections> Directions = new();
            public readonly List<Line> Lines = new();
        }

        /// <summary>Step 2, for every device read: the state mappings read.
        /// The state given, untouched, when nothing of the device is held or
        /// claimed.</summary>
        internal static CustomInputState Apply(Guid device, CustomInputState state)
        {
            // The device is read on this pass.
            _seen[device] = Volatile.Read(ref _lastPass);
            var table = Volatile.Read(ref _current);
            if (table == null || state == null || !table.TryGetValue(device, out var holds)) return state;
            return holds.Apply(state);
        }
    }
}
