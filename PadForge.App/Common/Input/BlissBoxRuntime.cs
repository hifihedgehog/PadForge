using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine;
using PadForge.Engine.Common.BlissBox;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The Bliss-Box ports PadForge talks to (issue #469), one per SDL row
    /// of a port while Read Bliss-Box Adapters is on. Step 1 pairs them with
    /// the rows by HID path, Step 2 merges what they read into the rows'
    /// states and hands them the rows' motor levels, and the wrapper's object
    /// list asks them for names.
    ///
    /// <para>The switch mirror follows <see cref="AnalogKeyboardRuntime"/>:
    /// written by <c>SettingsViewModel.BlissBoxEnabled</c> on the UI thread,
    /// read by the poll thread. The engine reads the same flag through
    /// <see cref="BlissBoxApi"/>.</para>
    /// </summary>
    internal static class BlissBoxRuntime
    {
        private static readonly object _lock = new();
        private static BlissBoxPort[] _ports = Array.Empty<BlissBoxPort>();
        private static int _generation;
        // Ports retired from Sync whose workers may still be sending their
        // final stop, by path. Under _lock.
        private static readonly List<(string Path, Task Done)> _retiring = new();

        /// <summary>The switch. A change counts in <see cref="Generation"/>,
        /// which brings Step 1's next pass forward to the poll thread's next
        /// cycle, and the motors change hands there, in order: the rows
        /// reopen, the ports open or retire, and the levels move
        /// (<c>InputManager.UpdateBlissBoxPorts</c>).</summary>
        public static bool Enabled
        {
            get => BlissBoxApi.Enabled;
            set
            {
                bool was = BlissBoxApi.Enabled;
                BlissBoxApi.Enabled = value;
                if (value != was) Interlocked.Increment(ref _generation);
            }
        }

        /// <summary>Counts the switch's changes, so Step 1 sees one that went
        /// off and on again between two of its passes.</summary>
        public static int Generation => Volatile.Read(ref _generation);

        /// <summary>True while a port retired for this path may still send
        /// its final stop: its worker has not let go of the channel. The
        /// motors go back to SDL only after, or that stop would end the level
        /// SDL was just given.</summary>
        public static bool IsRetiring(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (_lock)
            {
                _retiring.RemoveAll(r => r.Done.IsCompleted);
                foreach (var r in _retiring)
                    if (string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>The open ports, a snapshot safe to walk on any thread.</summary>
        public static BlissBoxPort[] Ports => Volatile.Read(ref _ports);

        /// <summary>Raised on a port's worker, on the poll thread when a port
        /// opens or retires, and on the stopping thread at shutdown. Handlers
        /// marshal to the UI themselves.</summary>
        public static event Action<BlissBoxPort> PortChanged;

        public static BlissBoxPort Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var port in Ports)
                if (string.Equals(port.Path, path, StringComparison.OrdinalIgnoreCase)) return port;
            return null;
        }

        public static BlissBoxPort Find(UserDevice ud)
            => ud != null && BlissBoxProtocol.IsPort(ud.VendorId, ud.ProdId) ? Find(ud.DevicePath) : null;

        /// <summary>A row a port is open for, identified.</summary>
        public readonly record struct Row(string Path, ushort ProductId, Guid InstanceGuid, uint SdlInstanceId);

        /// <summary>
        /// Poll thread: opens a port for every row in <paramref name="rows"/>
        /// that has none, and retires the ports whose row left or reconnected.
        /// The list is empty while the switch is off, which retires them all.
        /// Returns the ports opened, so the caller can hand their motors over
        /// from SDL.
        /// </summary>
        public static List<BlissBoxPort> Sync(IReadOnlyList<Row> rows)
        {
            List<BlissBoxPort> opened = null;
            List<BlissBoxPort> retired = null;
            lock (_lock)
            {
                var current = _ports;
                var next = new List<BlissBoxPort>(rows.Count);
                foreach (var port in current)
                {
                    bool kept = false;
                    foreach (var row in rows)
                        if (string.Equals(row.Path, port.Path, StringComparison.OrdinalIgnoreCase)
                            && row.InstanceGuid == port.InstanceGuid && row.SdlInstanceId == port.SdlInstanceId)
                        {
                            kept = true;
                            break;
                        }
                    if (kept) next.Add(port);
                    else (retired ??= new List<BlissBoxPort>()).Add(port);
                }
                foreach (var row in rows)
                {
                    bool open = false;
                    foreach (var port in next)
                        if (string.Equals(row.Path, port.Path, StringComparison.OrdinalIgnoreCase)) { open = true; break; }
                    if (open) continue;
                    var created = new BlissBoxPort(row.Path, row.ProductId, row.InstanceGuid, row.SdlInstanceId);
                    created.Changed += OnPortChanged;
                    created.Start();
                    next.Add(created);
                    (opened ??= new List<BlissBoxPort>()).Add(created);
                }
                if (opened != null || retired != null) Volatile.Write(ref _ports, next.ToArray());
            }
            if (retired != null)
            {
                foreach (var port in retired)
                {
                    port.Changed -= OnPortChanged;
                    // The join waits out a transfer in flight, never the poll
                    // thread's time.
                    var closing = port;
                    var done = Task.Run(closing.Dispose);
                    lock (_lock) _retiring.Add((closing.Path, done));
                    RaiseChanged(closing);
                }
            }
            if (opened != null)
                foreach (var port in opened) RaiseChanged(port);
            return opened;
        }

        /// <summary>Engine stop and app exit: every port stops its motors and
        /// closes, all at once.</summary>
        public static void Shutdown()
        {
            BlissBoxPort[] closing;
            lock (_lock)
            {
                closing = _ports;
                Volatile.Write(ref _ports, Array.Empty<BlissBoxPort>());
            }
            if (closing.Length == 0) return;
            foreach (var port in closing) port.Changed -= OnPortChanged;
            Parallel.ForEach(closing, port => { try { port.Dispose(); } catch { } });
            foreach (var port in closing) RaiseChanged(port);
        }

        private static void OnPortChanged(BlissBoxPort port) => RaiseChanged(port);

        private static void RaiseChanged(BlissBoxPort port)
        {
            try { PortChanged?.Invoke(port); } catch { }
        }

        /// <summary>The first pressure axis on a port's row: after every axis
        /// its joystick declares, and never below 8. -1 when twelve more do
        /// not fit.</summary>
        public static int PressureAxisBase(ISdlInputDevice device)
            => PressureAxisBase(DeclaredAxes(device));

        public static int PressureAxisBase(int declaredAxes)
        {
            int first = Math.Max(BlissBoxControllers.FirstPressureAxis, declaredAxes);
            return first + BlissBoxProtocol.PressureCount <= CustomInputState.MaxAxis ? first : -1;
        }

        private static int DeclaredAxes(ISdlInputDevice device)
            => device == null ? 0 : Math.Max(device.NumAxes, device.RawAxisCount);

        /// <summary>
        /// Step 2, right after SDL's read: the pressure bytes as axes, 0 at
        /// rest and 65535 at the bottom of the press, and the native poll's
        /// four directions on the buttons the firmware sends its own arrows
        /// on (<see cref="BlissBoxControllers.FirstArrowButton"/>), added to
        /// whatever the report already holds there. The state is a fresh
        /// pooled one each read, so a port that stops publishing leaves them
        /// at rest.
        ///
        /// <para>While the adapter searches, the last controller's trigger
        /// axes are put at rest. Its report then holds the idle values, 0x80
        /// on the axes (3.0 0x3103 to 0x3117, GPA 0x28B9 from RAM 0x02F0), or
        /// 0 on Z and Rz in the modes that clear them (3.0 0x310C, GPA
        /// 0x28C4), and the rest rule still reads those axes as the last
        /// controller's triggers (<see cref="RestsAtZero"/>), which would read
        /// 0x80 as half pressed.</para>
        /// </summary>
        public static void Merge(UserDevice ud, CustomInputState state)
        {
            if (state == null || ud == null || !OpenedRaw(ud.Device)) return;
            var port = Find(ud);
            if (port == null) return;
            var session = port.Session;
            if (session.LiveInfo is { } info)
            {
                int firstArrow = BlissBoxControllers.FirstArrowButton(info.Type, info.Major);
                // Arrows in the report itself, read before the poll's are
                // added, mean the adapter's latch sends them, and the native
                // poll can stop (BlissBoxSession.ArrowsLatched).
                if (session.NativeArrowsActive && ArrowsSent(state, firstArrow)) session.ArrowsLatched = true;
                MergeInto(state, session.Pressure, session.Arrows, PressureAxisBase(ud.Device), firstArrow);
                return;
            }
            if (session.Info is { Searching: true } && session.KnownInfo is { } known)
                RestTriggers(state, known);
        }

        /// <summary>True when any of the four arrow buttons is down in the
        /// state. For a 3.x PlayStation digital pad, whose layout leaves
        /// buttons 10 to 13 unnamed, only the firmware's latch sets them
        /// (0x3295 to 0x32A9).</summary>
        internal static bool ArrowsSent(CustomInputState state, int firstArrowButton)
        {
            if (firstArrowButton < 0) return false;
            for (int i = 0; i < BlissBoxControllers.ArrowNames.Length; i++)
                if (state.Buttons[firstArrowButton + i]) return true;
            return false;
        }

        /// <summary>Puts the axes this controller names as triggers at their
        /// rest, 0.</summary>
        internal static void RestTriggers(CustomInputState state, BlissBoxInfo known)
        {
            for (int axis = 0; axis < state.Axis.Length; axis++)
                if (BlissBoxControllers.IsTriggerAxis(known.Type, known.Major, axis)) state.Axis[axis] = 0;
        }

        /// <summary>A port's row as the switch left it: read raw, the shape
        /// the port's names, merge and rest rule describe. The switch changes
        /// the shape on Step 1's next pass, so a row keeps the port's rules
        /// until then, and a row read through SDL's gamepad mapping never
        /// takes them.</summary>
        private static bool OpenedRaw(ISdlInputDevice device)
            => device is SdlDeviceWrapper wrapper && wrapper.GameController == IntPtr.Zero;

        internal static void MergeInto(CustomInputState state, byte[] pressure, int arrows, int firstPressureAxis, int firstArrowButton)
        {
            if (pressure != null && firstPressureAxis >= 0)
                for (int i = 0; i < pressure.Length; i++)
                    state.Axis[firstPressureAxis + i] = pressure[i] * 257;

            if (arrows > 0 && firstArrowButton >= 0)
                for (int i = 0; i < BlissBoxControllers.ArrowNames.Length; i++)
                    if ((arrows & (1 << i)) != 0)
                        state.Buttons[firstArrowButton + i] = true;
        }

        /// <summary>The motor levels for a port, PadForge's 0 to 65535. The
        /// session keeps them and sends them once its channel is open, so the
        /// worker is woken only for a change. False when no port serves the
        /// path yet, so the caller tries again next frame, until Step 1
        /// opens one.</summary>
        public static bool SetRumble(string path, ushort large, ushort small)
        {
            var port = Find(path);
            if (port == null) return false;
            if (port.Session.SetRumble(large, small)) port.Wake();
            return true;
        }

        /// <summary>The hand-off from SDL: the port takes the row's recorded
        /// levels and tells both motors again, which on a GPA clears a
        /// one-motor pad's command-5 rumble that SDL's DirectInput effect can
        /// start there (<see cref="BlissBoxSession.ResendMotors"/>).</summary>
        public static void TakeMotors(string path, ushort large, ushort small)
        {
            var port = Find(path);
            if (port == null) return;
            port.Session.SetRumble(large, small);
            port.Session.ResendMotors();
            port.Wake();
        }

        /// <summary>Crash path: every port stops its motors, and the caller
        /// waits up to <paramref name="timeoutMs"/> for the workers to send
        /// it, since a dying process may not outlive an asynchronous
        /// stop.</summary>
        public static void StopMotorsNow(int timeoutMs)
        {
            var ports = Ports;
            if (ports.Length == 0) return;
            foreach (var port in ports)
            {
                port.Session.SetRumble(0, 0);
                port.Wake();
            }
            long end = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < end)
            {
                bool rest = true;
                foreach (var port in ports)
                    if (port.IsOpen && !port.Session.MotorsAtRest) { rest = false; break; }
                if (rest) return;
                Thread.Sleep(5);
            }
        }

        /// <summary>True when a port names this row's objects, so the picker
        /// shows its names rather than numbers: a controller a source lays
        /// out, or a DualShock 2, whose pressure axes carry names on every
        /// firmware. Any other controller keeps numbered names, as a raw
        /// joystick does.</summary>
        public static bool NamesObjects(UserDevice ud)
            => ud != null && OpenedRaw(ud.Device) && Find(ud)?.Session.LiveInfo is { } info
               && NamesObjectsFor(info);

        internal static bool NamesObjectsFor(BlissBoxInfo info)
            => BlissBoxControllers.HasLayout(info.Type, info.Major) || BlissBoxControllers.HasPressure(info.Type);

        /// <summary>True for a port's axes that rest at 0 and travel one way,
        /// a trigger's shape (the #443 rule): the pressure axes, which the
        /// merge leaves at 0 whenever no DualShock 2 is in the port, and the
        /// axes the controller in the port names as its triggers
        /// (<see cref="BlissBoxControllers.IsTriggerAxis"/>). Read raw, the
        /// port is a joystick, whose axes otherwise count as centered. The
        /// last controller identified answers through a reopen and while the
        /// adapter searches (<see cref="BlissBoxSession.KnownInfo"/>), and the
        /// merge puts those axes at rest while it searches, so a trigger at
        /// rest never reads as pressed there. The rule follows the row's
        /// shape, not the switch, so it holds until Step 1 reopens the
        /// row.</summary>
        public static bool RestsAtZero(UserDevice ud, int axis)
        {
            if (ud == null || !BlissBoxProtocol.IsPort(ud.VendorId, ud.ProdId) || !OpenedRaw(ud.Device)) return false;
            int first = PressureAxisBase(ud.Device);
            if (first >= 0 && axis >= first && axis < first + BlissBoxProtocol.PressureCount) return true;
            var session = Find(ud)?.Session;
            return (session?.LiveInfo ?? session?.KnownInfo) is { } info
                   && BlissBoxControllers.IsTriggerAxis(info.Type, info.Major, axis);
        }

        /// <summary>
        /// The wrapper's object list for a port, renamed for the controller in
        /// it: its buttons, sticks and hat per <see cref="BlissBoxControllers"/>,
        /// the four arrows on the buttons the firmware sends them on where the
        /// controller's own layout leaves those unnamed, and the twelve
        /// pressure axes appended for a DualShock 2. Unchanged when no port
        /// has identified a controller, or the row is not read raw.
        /// </summary>
        public static DeviceObjectItem[] ProvideObjects(ISdlInputDevice device, DeviceObjectItem[] items)
        {
            if (device == null || items == null || !OpenedRaw(device)) return items;
            var info = Find(device.DevicePath)?.Session.LiveInfo;
            return info == null ? items : NameObjects(info, DeclaredAxes(device), items);
        }

        internal static DeviceObjectItem[] NameObjects(BlissBoxInfo info, int declaredAxes, DeviceObjectItem[] items)
        {
            var list = new List<DeviceObjectItem>(items.Length + BlissBoxProtocol.PressureCount + 4);
            int firstArrow = BlissBoxControllers.FirstArrowButton(info.Type, info.Major);
            // Arrows the joystick does not declare are appended only where
            // PadForge's native poll fills them, a 3.x PlayStation digital
            // pad. The firmware writes only the buttons it declares, 24 on
            // both generations, and one that sends no arrows has none.
            var arrowSeen = new bool[BlissBoxControllers.ArrowNames.Length];
            if (firstArrow < 0 || info.Major != 3 || !BlissBoxControllers.IsPlayStationDigital(info.Type))
                Array.Fill(arrowSeen, true);
            foreach (var item in items)
            {
                string name = null;
                if (item.IsPov)
                {
                    if (item.InputIndex == 0) name = BlissBoxControllers.HatName(info.Type, info.Major);
                }
                else if (item.IsButton)
                {
                    // A button the controller's layout names keeps that name:
                    // the ColecoVision's keypad sits on buttons 10 to 13.
                    name = BlissBoxControllers.ButtonName(info.Type, info.Major, item.InputIndex);
                    int arrow = firstArrow < 0 ? -1 : item.InputIndex - firstArrow;
                    if (arrow >= 0 && arrow < arrowSeen.Length)
                    {
                        name ??= BlissBoxControllers.ArrowNames[arrow];
                        arrowSeen[arrow] = true;
                    }
                }
                else if (item.IsAxis && !item.IsSlider)
                {
                    name = BlissBoxControllers.AxisName(info.Type, info.Major, item.InputIndex);
                }
                if (name != null) item.Name = name;
                list.Add(item);
            }

            for (int arrow = 0; arrow < arrowSeen.Length; arrow++)
            {
                if (arrowSeen[arrow]) continue;
                int index = firstArrow + arrow;
                list.Add(new DeviceObjectItem
                {
                    InputIndex = index,
                    ObjectTypeGuid = ObjectGuid.Button,
                    Name = BlissBoxControllers.ArrowNames[arrow],
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    Offset = (declaredAxes + index) * 4,
                });
            }

            int first = PressureAxisBase(declaredAxes);
            if (BlissBoxControllers.HasPressure(info.Type) && first >= 0)
            {
                var names = BlissBoxControllers.PressureNames;
                for (int i = 0; i < names.Count; i++)
                {
                    list.Add(new DeviceObjectItem
                    {
                        InputIndex = first + i,
                        // A non-slider axis GUID, so the "Axis N" descriptor
                        // reads it from Axis[] (the #193 extra-axis rule).
                        ObjectTypeGuid = ObjectGuid.ZAxis,
                        Name = names[i],
                        ObjectType = DeviceObjectTypeFlags.AbsoluteAxis,
                        Offset = (first + i) * 4,
                    });
                }
            }
            return list.ToArray();
        }
    }
}
