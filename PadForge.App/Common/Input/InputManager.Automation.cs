using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Automation (#600): devices programs add, and the pass hook
        // ─────────────────────────────────────────────

        /// <summary>
        /// Called on the poll thread at the end of Step 2 on every pass, the
        /// idle ones included, with the devices read on it, their count and
        /// the run the pass belongs to. Automation's watcher queues events
        /// from it and returns: it never waits on a program. Null while no
        /// program follows anything, so a pass with no follower pays one read
        /// of this field.
        /// </summary>
        internal static volatile Action<UserDevice[], int, AutomationPassRun> AutomationPass;

        /// <summary>The run a pass belongs to: a token of the engine, never
        /// the engine itself, so a retired run the watcher keeps holds none
        /// of it, and the poll loop's stamp. Stop gives up its wait for the
        /// poll thread after 3 s, so a pass of a retired run can still reach
        /// the watcher after the stop, and the watcher tells it apart by
        /// this.</summary>
        internal readonly record struct AutomationPassRun(object Engine, int Generation);

        /// <summary>This engine's token in <see cref="AutomationPassRun"/>.</summary>
        private readonly object _automationRunToken = new object();

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
            lock (_programDevices)
            {
                // A manager that stopped takes no device: the next start's
                // manager registers it.
                if (_programDevicesReleased) return;
                _programDevices.Add(device);
                // Under the set's lock, so a release that runs meanwhile
                // takes this handler off too.
                device.CapabilitiesChanged -= OnProgramDeviceCapabilitiesChanged;
                device.CapabilitiesChanged += OnProgramDeviceCapabilitiesChanged;
            }

            UserDevice ud = FindOrCreateUserDevice(device.InstanceGuid, device.ProductGuid);
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                Load(ud, device);
                ud.IsOnline = true;
            }

            DevicesUpdated?.Invoke(this, EventArgs.Empty);
        }

        // The program devices this manager took, so the manager takes its
        // handler off each when it goes (ReleaseProgramDevices). The device
        // outlives the manager: the registry hands it to the next start's.
        private readonly HashSet<ProgramDevice> _programDevices = new();
        private bool _programDevicesReleased;

        /// <summary>Takes this manager's handler off every program device it
        /// registered, at its Dispose, so a device the next start's manager
        /// takes raises no stopped manager's handler, which would root that
        /// manager and walk the device list once more at each reshape or
        /// rename.</summary>
        internal void ReleaseProgramDevices()
        {
            lock (_programDevices)
            {
                _programDevicesReleased = true;
                foreach (var device in _programDevices) device.CapabilitiesChanged -= OnProgramDeviceCapabilitiesChanged;
                _programDevices.Clear();
            }
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
            lock (_programDevices)
            {
                _programDevices.Remove(device);
                device.CapabilitiesChanged -= OnProgramDeviceCapabilitiesChanged;
            }
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
    /// The values programs hold controls of devices at (#600), and the
    /// controls their hotkeys take from games (#600 item 23), as the poll
    /// thread reads them. A program's <c>own</c> line puts a value over a
    /// button, an axis, a slider, a hat or one direction of a hat of any
    /// device, a <c>set</c> line
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
    /// <para>The table mappings read is rebuilt only when a step lands, into
    /// the tables it kept from the last rebuild when the held and claimed
    /// controls stay the same, and is read on each device with one read and
    /// no lock.</para>
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
        /// hat, a held hat direction (index = hat * 4 + 0 for Up, 1 Right, 2
        /// Down, 3 Left), a claimed button, a claimed hat direction (the same
        /// index), and a claimed line (index = the line's own number).</summary>
        internal const char HoldButton = 'b', HoldAxis = 'a', HoldHat = 'h', HoldDirection = 'd',
            ClaimButton = 'B', ClaimDirection = 'D', ClaimLine = 'L';

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
        /// Its controls are written once before it is published. A rebuild
        /// may put a new hold in a control's slot (<see cref="TakeIfSame"/>),
        /// and its pool and the set holds' marks are the poll
        /// thread's.</summary>
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
                Controls = ControlsOf(this);
            }

            internal DeviceHolds((int, Hold)[] buttons, (int, Hold)[] axes, (int, Hold)[] hats,
                (int Hat, (HatDirections Direction, Hold Hold)[] Holds)[] directions,
                int[] claimedButtons, (int Hat, HatDirections Directions)[] claimedDirections, Line[] lines)
            {
                Buttons = buttons;
                Axes = axes;
                Hats = hats;
                Directions = directions;
                ClaimedButtons = claimedButtons;
                ClaimedDirections = claimedDirections;
                Lines = lines;
                Controls = ControlsOf(this);
            }

            /// <summary>The session that holds or claims the device's
            /// controls, the first one when several do. 0 when none is
            /// known.</summary>
            public int Owner { get; init; }

            /// <summary>Every control held or claimed, as the output causes
            /// name it: <c>b</c> and a button, <c>a</c> and an axis (a slider
            /// counts on past the axes), <c>h</c> and a hat.</summary>
            public readonly HashSet<(char Kind, int Index)> Controls;

            private static HashSet<(char Kind, int Index)> ControlsOf(DeviceHolds d)
            {
                var set = new HashSet<(char, int)>();
                foreach (var (i, _) in d.Buttons) set.Add(('b', i));
                foreach (var (i, _) in d.Axes) set.Add(('a', i));
                foreach (var (i, _) in d.Hats) set.Add(('h', i));
                foreach (var (hat, _) in d.Directions) set.Add(('h', hat));
                foreach (int i in d.ClaimedButtons) set.Add(('b', i));
                foreach (var (hat, _) in d.ClaimedDirections) set.Add(('h', hat));
                foreach (var line in d.Lines) set.Add(('a', line.Axis));
                return set;
            }

            /// <summary>Buttons by index, below 256.</summary>
            public readonly (int Index, Hold Hold)[] Buttons;

            /// <summary>Axes 0 to 23 and sliders 24 to 31, in
            /// <see cref="CustomInputState"/>'s range.</summary>
            public readonly (int Index, Hold Hold)[] Axes;

            /// <summary>Hats 0 to 3, in hundredths of a degree or -1.</summary>
            public readonly (int Index, Hold Hold)[] Hats;

            /// <summary>Hat directions held on their own (#600 item 21), by
            /// hat: each pressed (1) or held released (0) over the hat's
            /// whole hold, or over the device's own hat when it has none,
            /// beside the hat's other directions.</summary>
            public readonly (int Hat, (HatDirections Direction, Hold Hold)[] Holds)[] Directions
                = Array.Empty<(int, (HatDirections, Hold)[])>();

            public readonly int[] ClaimedButtons = Array.Empty<int>();
            public readonly (int Hat, HatDirections Directions)[] ClaimedDirections = Array.Empty<(int, HatDirections)>();
            public readonly Line[] Lines = Array.Empty<Line>();

            private PooledInputStatePair _pool;

            /// <summary>
            /// Under the pass lock, at a rebuild: takes the holds of
            /// <paramref name="parts"/> in place of this table's own when
            /// they hold and claim the same controls of the same owner, and
            /// answers whether it did. Each hold goes into the slot of its
            /// own control, the index there unchanged, so a pass reading the
            /// table meanwhile reads the old hold or the new one, and
            /// <see cref="Controls"/>, which the output causes keep from one
            /// pass to the next, stays as it was. A control that two owners
            /// hold or claim at once is never matched: the table is made
            /// again.
            /// </summary>
            internal bool TakeIfSame(Parts parts)
            {
                if (parts.Owner != Owner) return false;
                if (Repeats(parts.Buttons) || Repeats(parts.Axes) || Repeats(parts.Hats) || Repeats(parts.HeldDirections)) return false;
                for (int i = 0; i < parts.Claimed.Count; i++)
                    for (int j = i + 1; j < parts.Claimed.Count; j++)
                        if (parts.Claimed[i] == parts.Claimed[j]) return false;
                if (!SameIndexes(Buttons, parts.Buttons) || !SameIndexes(Axes, parts.Axes) || !SameIndexes(Hats, parts.Hats)) return false;
                if (ClaimedButtons.Length != parts.Claimed.Count) return false;
                foreach (int index in parts.Claimed)
                    if (Array.IndexOf(ClaimedButtons, index) < 0) return false;
                if (ClaimedDirections.Length != parts.Directions.Count) return false;
                foreach (var (hat, directions) in parts.Directions)
                    if (!Claims(hat, directions)) return false;
                if (Lines.Length != parts.Lines.Count) return false;
                for (int i = 0; i < Lines.Length; i++)
                    if (Lines[i].Axis != parts.Lines[i].Axis) return false;
                int held = 0;
                foreach (var (_, holds) in Directions) held += holds.Length;
                if (held != parts.HeldDirections.Count) return false;
                foreach (var (index, _) in parts.HeldDirections)
                    if (!FindDirection(index, out _, out _)) return false;

                Take(Buttons, parts.Buttons);
                Take(Axes, parts.Axes);
                Take(Hats, parts.Hats);
                for (int i = 0; i < Lines.Length; i++)
                    if (!ReferenceEquals(Lines[i], parts.Lines[i])) Lines[i] = parts.Lines[i];
                foreach (var (index, hold) in parts.HeldDirections)
                {
                    FindDirection(index, out int at, out int slot);
                    var holds = Directions[at].Holds;
                    if (!ReferenceEquals(holds[slot].Hold, hold)) holds[slot] = (holds[slot].Direction, hold);
                }
                return true;
            }

            private static bool Repeats(List<(int Index, Hold Hold)> list)
            {
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                        if (list[i].Index == list[j].Index) return true;
                return false;
            }

            private static int Find((int Index, Hold Hold)[] mine, int index)
            {
                for (int i = 0; i < mine.Length; i++)
                    if (mine[i].Index == index) return i;
                return -1;
            }

            /// <summary>The same controls both ways, each held once.</summary>
            private static bool SameIndexes((int Index, Hold Hold)[] mine, List<(int, Hold)> parts)
            {
                if (mine.Length != parts.Count) return false;
                foreach (var (index, _) in parts)
                    if (Find(mine, index) < 0) return false;
                foreach (var (index, _) in mine)
                {
                    bool found = false;
                    foreach (var (other, _) in parts)
                        if (other == index) { found = true; break; }
                    if (!found) return false;
                }
                return true;
            }

            private static void Take((int Index, Hold Hold)[] mine, List<(int, Hold)> parts)
            {
                foreach (var (index, hold) in parts)
                {
                    int at = Find(mine, index);
                    if (!ReferenceEquals(mine[at].Hold, hold)) mine[at] = (index, hold);
                }
            }

            private bool Claims(int hat, HatDirections directions)
            {
                foreach (var (h, d) in ClaimedDirections)
                    if (h == hat && d == directions) return true;
                return false;
            }

            /// <summary>Where a held direction's key (hat * 4 + direction)
            /// sits in <see cref="Directions"/>.</summary>
            private bool FindDirection(int index, out int at, out int slot)
            {
                int hat = index / 4;
                var direction = (HatDirections)(1 << (index % 4));
                for (at = 0; at < Directions.Length; at++)
                {
                    if (Directions[at].Hat != hat) continue;
                    var holds = Directions[at].Holds;
                    for (slot = 0; slot < holds.Length; slot++)
                        if (holds[slot].Direction == direction) return true;
                }
                at = slot = -1;
                return false;
            }

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
                // A hat's held directions go in together, over the hat as it
                // stands now. Up and Down together cancel, as Left and Right
                // do, and a hat keeps its exact angle until a held direction
                // changes which directions it holds.
                foreach (var (hat, holds) in Directions)
                {
                    if ((uint)hat >= (uint)s.Povs.Length) continue;
                    var own = PlainUnits.HatToDirections(read.Povs[hat]);
                    var was = PlainUnits.HatToDirections(s.Povs[hat]);
                    var now = was;
                    foreach (var (direction, hold) in holds)
                        if (InForce(hold, (own & direction) != 0 ? 1 : 0))
                            now = hold.Value != 0 ? now | direction : now & ~direction;
                    if (now != was) s.Povs[hat] = PlainUnits.DirectionsToHat(now);
                }
                return s;
            }
        }

        /// <summary>
        /// A program's lease (#600 item 24), judged on this table's clock,
        /// where its holds and claims are applied. While the program has said
        /// nothing for <see cref="Span"/> and runs no line, its holds and
        /// claims are left out of the table: the first pass past the span
        /// lets them go, and the sessions' clock does it while no pass runs.
        /// A script's come back at its next line, all at once. Any other
        /// program's stay out until its session lets go of them for good.
        /// </summary>
        internal sealed class Lease
        {
            public Lease(int ms, bool comesBack)
            {
                Span = ms * Stopwatch.Frequency / 1000;
                ComesBack = comesBack;
                _heard = Now();
            }

            /// <summary>How long the program may say nothing, in clock
            /// ticks.</summary>
            public readonly long Span;

            /// <summary>Whether what the program holds and claims comes
            /// back at its next line: a script's does.</summary>
            public readonly bool ComesBack;

            private long _heard;
            private int _running;

            /// <summary>The program sent a line.</summary>
            public void Heard() => Volatile.Write(ref _heard, Now());

            /// <summary>A line of the program's runs: the program waits on
            /// PadForge meanwhile, and its lines behind it, its beats
            /// among them, wait in the pipe.</summary>
            public void Begin() => Interlocked.Increment(ref _running);

            /// <summary>The line that ran is done, and counts as
            /// heard.</summary>
            public void End()
            {
                Volatile.Write(ref _heard, Now());
                Interlocked.Decrement(ref _running);
            }

            /// <summary>Whether a line of the program's runs now.</summary>
            public bool Runs => Volatile.Read(ref _running) > 0;

            /// <summary>Whether the program said nothing for its span at
            /// <paramref name="now"/>, with no line of its running.</summary>
            public bool Silent(long now) => Volatile.Read(ref _running) == 0 && now - Volatile.Read(ref _heard) > Span;
        }

        /// <summary>An owner's lease and what the table did with it: whether
        /// the owner holds or claims anything, and whether the table leaves
        /// it out. Both are written under the pass lock and read by the pass
        /// without it.</summary>
        private sealed class Leased
        {
            public Leased(int owner, Lease lease)
            {
                Owner = owner;
                Lease = lease;
            }

            public readonly int Owner;
            public readonly Lease Lease;
            public volatile bool Holds;
            public volatile bool Out;

            /// <summary>Whether the table leaves the owner out at
            /// <paramref name="now"/>: a script's while its lease is
            /// silent, any other program's from then on.</summary>
            public bool OutAt(long now) => Holds && ((Out && !Lease.ComesBack) || Lease.Silent(now));
        }

        // The leases, copied on each change, so a pass reads them with one
        // read and no lock.
        private static Leased[] _leased = Array.Empty<Leased>();

        /// <summary>Judges an owner's holds and claims by its lease from now
        /// on, in place of any lease it had.</summary>
        internal static void Keep(int owner, Lease lease)
        {
            lock (_passLock)
            {
                var list = new List<Leased>(_leased.Length + 1);
                foreach (var e in _leased) if (e.Owner != owner) list.Add(e);
                list.Add(new Leased(owner, lease) { Holds = _owners.ContainsKey(owner) });
                Volatile.Write(ref _leased, list.ToArray());
            }
        }

        /// <summary>Forgets an owner's lease: its session ended.</summary>
        internal static void Forget(int owner)
        {
            lock (_passLock)
            {
                var list = new List<Leased>(_leased.Length);
                foreach (var e in _leased) if (e.Owner != owner) list.Add(e);
                if (list.Count == _leased.Length) return;
                Volatile.Write(ref _leased, list.ToArray());
                if (_owners.ContainsKey(owner))
                {
                    _dirty = true;
                    Rebuild();
                }
            }
        }

        /// <summary>Whether the table leaves the owner's holds and claims
        /// out now, its lease having lapsed.</summary>
        internal static bool IsOut(int owner)
        {
            foreach (var e in Volatile.Read(ref _leased))
                if (e.Owner == owner) return e.Out;
            return false;
        }

        /// <summary>The owner sent a line (its lease's
        /// <see cref="Lease.Heard"/> is called first): a script whose lease
        /// had lapsed has everything it holds and claims back in the table,
        /// at once. True when the owner's holds and claims were out, which
        /// for any other program they stay until its session lets go of
        /// them. Costs one read when the lease stands.</summary>
        internal static bool Renew(int owner)
        {
            foreach (var e in Volatile.Read(ref _leased))
            {
                if (e.Owner != owner) continue;
                if (!e.Out) return false;
                if (e.Lease.ComesBack)
                {
                    long now = Now();
                    lock (_passLock) Advance(now, inPass: false);
                }
                return true;
            }
            return false;
        }

        /// <summary>Whether a lease turned since the table was built: one
        /// the table keeps that ran out, or one it leaves out that a line
        /// renewed. Reads each lease, takes no lock and makes
        /// nothing.</summary>
        private static bool LeaseTurns(long now)
        {
            foreach (var e in Volatile.Read(ref _leased))
                if (e.Holds && e.OutAt(now) != e.Out) return true;
            return false;
        }

        /// <summary>Under the lock: each lease as the table stands now,
        /// and the table to rebuild when one turned.</summary>
        private static void JudgeLeases(long now)
        {
            foreach (var e in _leased)
            {
                e.Holds = _owners.ContainsKey(e.Owner);
                bool left = e.OutAt(now);
                if (left == e.Out) continue;
                e.Out = left;
                _dirty = true;
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
        /// forgets the steps that wait and the leases. With a table, puts
        /// that table in force alone. Tests use it. A host that goes lets go
        /// of its own sessions' alone (AutomationSessions.Dispose).</summary>
        internal static void Publish(Dictionary<Guid, DeviceHolds> table)
        {
            lock (_passLock)
            {
                while (Sent.TryDequeue(out _)) { }
                Volatile.Write(ref _sentCount, 0);
                _waiting.Clear();
                Volatile.Write(ref _waitingCount, 0);
                _owners.Clear();
                _parts.Clear();
                _lastHolds.Clear();
                Array.Clear(_lastTables);
                Volatile.Write(ref _leased, Array.Empty<Leased>());
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
        /// step. A device a pass reads waits for the pass. Leases are
        /// judged here too, so while no pass runs a lapse lets go at the
        /// sessions' clock, within its 100 ms step of the span.</summary>
        internal static void LandDue()
        {
            long now = Now();
            lock (_passLock) Advance(now, inPass: false);
        }

        private static bool Polled(Guid device, long now)
            => _seen.TryGetValue(device, out long at) && now - at <= ProgramDevice.NotPolledTicks;

        /// <summary>The start of a pass, on the poll thread, before any
        /// device is read: the steps that are due land, one change per
        /// control, and a lease that ran out since the last pass lets go of
        /// its holds and claims (#600 item 24), on the clock that times the
        /// pass. Costs a read when nothing waits and a read for each lease,
        /// and makes nothing. A step that lands makes nothing either while
        /// each device's held and claimed controls are a set one of its last
        /// <see cref="KeptSets"/> tables holds: a held value that changes, a
        /// hold let go and taken again, a button tapped beside a held stick.
        /// Those tables and the tables of devices are kept, the new holds in
        /// place (<see cref="Rebuild"/>). A set that is not among them makes
        /// a new table, which takes the oldest one's place.</summary>
        internal static void BeginPass()
        {
            long now = Now();
            Volatile.Write(ref _lastPass, now);
            if (!Pending && !LeaseTurns(now)) return;
            lock (_passLock) Advance(now, inPass: true);
        }

        /// <summary>Under the lock: takes what was sent and lands what is
        /// due, in order. In a pass (<paramref name="inPass"/>) each control
        /// changes once. Out of one, only the steps of devices no pass reads
        /// land. Then each lease is judged. The table is rebuilt when
        /// anything landed or a lease turned.</summary>
        private static void Advance(long now, bool inPass)
        {
            while (Sent.TryDequeue(out var step))
            {
                Interlocked.Decrement(ref _sentCount);
                if (step.Drop)
                {
                    int owner = step.Owner;
                    for (int w = _waiting.Count - 1; w >= 0; w--)
                        if (_waiting[w].Owner == owner) _waiting.RemoveAt(w);
                    if (_owners.Remove(owner, out var gone))
                    {
                        Spare(gone);
                        _dirty = true;
                    }
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
                        _owners[step.Owner] = held = _spareHeld.Count > 0 ? _spareHeld.Pop() : new Dictionary<(Guid, char, int), object>();
                    if (step.Value == null) held.Remove(key);
                    else held[key] = step.Value;
                    if (held.Count == 0)
                    {
                        _owners.Remove(step.Owner);
                        Spare(held);
                    }
                    _changed.Add(key);
                    _dirty = true;
                }
                _waiting.Clear();
                _waiting.AddRange(_kept);
                _kept.Clear();
            }
            Volatile.Write(ref _waitingCount, _waiting.Count);
            JudgeLeases(now);
            if (_dirty) Rebuild();
        }

        /// <summary>Under the lock: whether the table leaves an owner out,
        /// its lease having lapsed.</summary>
        private static bool LeftOut(int owner)
        {
            foreach (var e in _leased)
                if (e.Owner == owner) return e.Out;
            return false;
        }

        // Kept from one rebuild to the next, under the pass lock (#600): each
        // device's parts, its last tables with each table's own buffers, the
        // last tables of devices, and the owners' emptied lists. A step that
        // changes a held value, or a device's controls that come back to a
        // set among its last ones, then make nothing on the pass that lands
        // them.
        private static readonly Dictionary<Guid, Parts> _parts = new();
        private static readonly Dictionary<Guid, KeptHolds> _lastHolds = new();
        private static readonly Dictionary<Guid, DeviceHolds>[] _lastTables = new Dictionary<Guid, DeviceHolds>[KeptSets];
        private static int _nextTable;
        private static readonly Stack<Dictionary<(Guid, char, int), object>> _spareHeld = new();

        /// <summary>How many devices the kept parts and tables cover before
        /// they are let go and made again.</summary>
        private const int MostKept = 64;

        /// <summary>How many sets of controls a device's kept tables cover,
        /// and how many tables of devices are kept: a program that moves a
        /// device among up to this many sets, a held stick with a button
        /// tapped beside it among them, makes nothing once each set was
        /// seen.</summary>
        internal const int KeptSets = 4;

        /// <summary>A device's last tables, the next new one taking the
        /// oldest one's place.</summary>
        private sealed class KeptHolds
        {
            public readonly DeviceHolds[] Holds = new DeviceHolds[KeptSets];
            public int Next;
        }

        /// <summary>Under the lock: the device's kept table that takes these
        /// parts, or one made anew and kept in place of the oldest.</summary>
        private static DeviceHolds HoldsFor(Guid device, Parts parts)
        {
            if (!_lastHolds.TryGetValue(device, out var kept)) _lastHolds[device] = kept = new KeptHolds();
            foreach (var holds in kept.Holds)
                if (holds != null && holds.TakeIfSame(parts)) return holds;
            var made = Build(parts);
            kept.Holds[kept.Next] = made;
            kept.Next = (kept.Next + 1) % KeptSets;
            return made;
        }

        /// <summary>Under the lock: a kept table of devices that holds just
        /// the tables this rebuild gave, or null.</summary>
        private static Dictionary<Guid, DeviceHolds> KeptTable(int used)
        {
            foreach (var table in _lastTables)
            {
                if (table == null || table.Count != used) continue;
                bool same = true;
                foreach (var (device, parts) in _parts)
                {
                    if (!parts.Used) continue;
                    if (!table.TryGetValue(device, out var was) || !ReferenceEquals(was, parts.Built))
                    {
                        same = false;
                        break;
                    }
                }
                if (same) return table;
            }
            return null;
        }

        private static void Spare(Dictionary<(Guid, char, int), object> held)
        {
            held.Clear();
            if (_spareHeld.Count < 16) _spareHeld.Push(held);
        }

        /// <summary>Under the lock: the table mappings read, from what every
        /// owner holds and claims, but an owner whose lease lapsed. A device
        /// whose held and claimed controls are the ones one of its last
        /// <see cref="KeptSets"/> tables has keeps that table with the new
        /// holds in place (<see cref="DeviceHolds.TakeIfSame"/>), and a kept
        /// table of devices that holds those same tables is published again,
        /// so neither is made again.</summary>
        private static void Rebuild()
        {
            _dirty = false;
            foreach (var kept in _parts.Values) kept.Clear();
            foreach (var (owner, held) in _owners)
            {
                if (LeftOut(owner)) continue;
                foreach (var ((device, kind, index), value) in held)
                {
                    if (!_parts.TryGetValue(device, out var parts)) _parts[device] = parts = new Parts();
                    if (!parts.Used)
                    {
                        parts.Used = true;
                        parts.Owner = owner;
                    }
                    switch (kind)
                    {
                        case HoldButton: parts.Buttons.Add((index, (Hold)value)); break;
                        case HoldAxis: parts.Axes.Add((index, (Hold)value)); break;
                        case HoldHat: parts.Hats.Add((index, (Hold)value)); break;
                        case HoldDirection: parts.HeldDirections.Add((index, (Hold)value)); break;
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
            int used = 0;
            foreach (var (device, parts) in _parts)
            {
                if (!parts.Used) continue;
                used++;
                parts.Built = HoldsFor(device, parts);
            }
            Dictionary<Guid, DeviceHolds> table = null;
            if (used > 0)
            {
                table = KeptTable(used);
                if (table == null)
                {
                    table = new Dictionary<Guid, DeviceHolds>(used);
                    foreach (var (device, parts) in _parts)
                        if (parts.Used) table[device] = parts.Built;
                    _lastTables[_nextTable] = table;
                    _nextTable = (_nextTable + 1) % KeptSets;
                }
            }
            Volatile.Write(ref _current, table);
            if (_parts.Count > MostKept)
            {
                // Many devices came and went: the kept parts and tables go,
                // and the next rebuild makes what it needs.
                _parts.Clear();
                _lastHolds.Clear();
                Array.Clear(_lastTables);
            }
        }

        /// <summary>A device's table made anew, for a set of controls its
        /// last table does not hold.</summary>
        private static DeviceHolds Build(Parts parts)
        {
            var directions = new (int, HatDirections)[parts.Directions.Count];
            int at = 0;
            foreach (var (hat, d) in parts.Directions) directions[at++] = (hat, d);
            return new DeviceHolds(parts.Buttons.ToArray(), parts.Axes.ToArray(), parts.Hats.ToArray(),
                HeldByHat(parts.HeldDirections), parts.Claimed.ToArray(), directions, parts.Lines.ToArray())
            { Owner = parts.Owner };
        }

        /// <summary>Held directions, by key, as the table keeps them: each
        /// hat once, with its directions.</summary>
        private static (int Hat, (HatDirections Direction, Hold Hold)[] Holds)[] HeldByHat(List<(int Index, Hold Hold)> held)
        {
            if (held.Count == 0) return Array.Empty<(int, (HatDirections, Hold)[])>();
            var byHat = new SortedDictionary<int, List<(HatDirections, Hold)>>();
            foreach (var (index, hold) in held)
            {
                if (!byHat.TryGetValue(index / 4, out var list)) byHat[index / 4] = list = new();
                list.Add(((HatDirections)(1 << (index % 4)), hold));
            }
            var hats = new List<(int, (HatDirections, Hold)[])>(byHat.Count);
            foreach (var (hat, list) in byHat) hats.Add((hat, list.ToArray()));
            return hats.ToArray();
        }

        /// <summary>A device's holds and claims gathered at a rebuild, kept
        /// for the next one.</summary>
        internal sealed class Parts
        {
            public readonly List<(int, Hold)> Buttons = new(), Axes = new(), Hats = new(), HeldDirections = new();
            public readonly List<int> Claimed = new();
            public readonly Dictionary<int, HatDirections> Directions = new();
            public readonly List<Line> Lines = new();
            public int Owner;

            /// <summary>Something of the device is held or claimed on this
            /// rebuild.</summary>
            public bool Used;

            /// <summary>The device's table this rebuild gave.</summary>
            public DeviceHolds Built;

            public void Clear()
            {
                Buttons.Clear();
                Axes.Clear();
                Hats.Clear();
                HeldDirections.Clear();
                Claimed.Clear();
                Directions.Clear();
                Lines.Clear();
                Owner = 0;
                Used = false;
                Built = null;
            }
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
