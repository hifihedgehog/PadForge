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

        /// <summary>The switch. The ports themselves open and close on Step
        /// 1's next pass, so the motors change hands at once here: switched
        /// on, an effect SDL started on a port stops, since SDL's rumble no
        /// longer reaches the port to stop it, and the port's hand-off then
        /// clears whatever that stop left on a GPA
        /// (<see cref="TakeMotors"/>). Switched off, each open port stops its
        /// motors, since the game's next zero goes to SDL.</summary>
        public static bool Enabled
        {
            get => BlissBoxApi.Enabled;
            set
            {
                bool was = BlissBoxApi.Enabled;
                BlissBoxApi.Enabled = value;
                if (value == was) return;
                Interlocked.Increment(ref _generation);
                if (value) StopSdlRumbleOnPorts();
                else
                    foreach (var port in Ports)
                        if (port.Session.SetRumble(0, 0)) port.Wake();
            }
        }

        private static void StopSdlRumbleOnPorts()
        {
            var devices = SettingsManager.UserDevices;
            if (devices == null) return;
            var wrappers = new List<SdlDeviceWrapper>();
            lock (devices.SyncRoot)
                foreach (var ud in devices.Items)
                    if (ud?.Device is SdlDeviceWrapper wrapper && BlissBoxProtocol.IsPort(ud.VendorId, ud.ProdId))
                        wrappers.Add(wrapper);
            foreach (var wrapper in wrappers)
                try { wrapper.StopSdlRumble(); } catch { }
        }

        /// <summary>Counts the switch's changes, so Step 1 sees one that went
        /// off and on again between two of its passes.</summary>
        public static int Generation => Volatile.Read(ref _generation);

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
                    Task.Run(closing.Dispose);
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
        /// </summary>
        public static void Merge(UserDevice ud, CustomInputState state)
        {
            var port = Find(ud);
            if (port == null || state == null) return;
            var session = port.Session;
            if (session.LiveInfo is not { } info) return;
            MergeInto(state, session.Pressure, session.Arrows, PressureAxisBase(ud.Device),
                BlissBoxControllers.FirstArrowButton(info.Type, info.Major));
        }

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
        /// shows its names rather than numbers.</summary>
        public static bool NamesObjects(UserDevice ud)
            => Enabled && Find(ud)?.Session.LiveInfo != null;

        /// <summary>True for a port's axes that rest at 0 and travel one way,
        /// a trigger's shape (the #443 rule): the pressure axes, which the
        /// merge leaves at 0 whenever no DualShock 2 is in the port, and the
        /// axes the controller in the port names as its triggers
        /// (<see cref="BlissBoxControllers.IsTriggerAxis"/>). Read raw, the
        /// port is a joystick, whose axes otherwise count as centered. The
        /// last controller identified answers through a reopen or a moment of
        /// searching (<see cref="BlissBoxSession.KnownInfo"/>), so a trigger
        /// at rest never reads as pressed there.</summary>
        public static bool RestsAtZero(UserDevice ud, int axis)
        {
            if (ud == null || !Enabled || !BlissBoxProtocol.IsPort(ud.VendorId, ud.ProdId)) return false;
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
        /// has identified a controller.
        /// </summary>
        public static DeviceObjectItem[] ProvideObjects(ISdlInputDevice device, DeviceObjectItem[] items)
        {
            if (device == null || items == null || !Enabled) return items;
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
