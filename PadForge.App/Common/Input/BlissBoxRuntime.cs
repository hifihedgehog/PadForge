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

        public static bool Enabled
        {
            get => BlissBoxApi.Enabled;
            set => BlissBoxApi.Enabled = value;
        }

        /// <summary>The open ports, a snapshot safe to walk on any thread.</summary>
        public static BlissBoxPort[] Ports => Volatile.Read(ref _ports);

        /// <summary>Raised on a port's worker, or on the poll thread when a
        /// port opens or retires. Handlers marshal to the UI themselves.</summary>
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
        public readonly record struct Row(string Path, ushort ProductId, Guid InstanceGuid);

        /// <summary>
        /// Poll thread: opens a port for every row in <paramref name="rows"/>
        /// that has none, and retires the ports whose row left. The list is
        /// empty while the switch is off, which retires them all. Returns the
        /// ports opened, so the caller can hand their motors over from SDL.
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
                            && row.InstanceGuid == port.InstanceGuid)
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
                    var created = new BlissBoxPort(row.Path, row.ProductId, row.InstanceGuid);
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
        /// four directions as buttons 20 to 23. The state is a fresh pooled
        /// one each read, so a port that stops publishing leaves them at rest.
        /// </summary>
        public static void Merge(UserDevice ud, CustomInputState state)
        {
            var port = Find(ud);
            if (port == null || state == null) return;
            var session = port.Session;
            if (session.LiveInfo == null) return;
            MergeInto(state, session.Pressure, session.Arrows, PressureAxisBase(ud.Device));
        }

        internal static void MergeInto(CustomInputState state, byte[] pressure, int arrows, int firstPressureAxis)
        {
            if (pressure != null && firstPressureAxis >= 0)
                for (int i = 0; i < pressure.Length; i++)
                    state.Axis[firstPressureAxis + i] = pressure[i] * 257;

            if (arrows > 0)
                for (int i = 0; i < BlissBoxControllers.ArrowNames.Length; i++)
                    if ((arrows & (1 << i)) != 0)
                        state.Buttons[BlissBoxControllers.FirstArrowButton + i] = true;
        }

        /// <summary>The motor levels for a port, PadForge's 0 to 65535. False
        /// when no open port serves the path yet, so the caller retries.</summary>
        public static bool SetRumble(string path, ushort large, ushort small)
        {
            var port = Find(path);
            if (port == null) return false;
            port.Session.SetRumble(large, small);
            port.Wake();
            return port.IsOpen;
        }

        /// <summary>True when a port names this row's objects, so the picker
        /// shows its names rather than numbers.</summary>
        public static bool NamesObjects(UserDevice ud)
            => Enabled && Find(ud)?.Session.LiveInfo != null;

        /// <summary>
        /// The wrapper's object list for a port, renamed for the controller in
        /// it: its buttons, sticks and hat per <see cref="BlissBoxControllers"/>,
        /// buttons 20 to 23 as the four arrows on every port, and the twelve
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
            var arrowSeen = new bool[BlissBoxControllers.ArrowNames.Length];
            foreach (var item in items)
            {
                string name = null;
                if (item.IsPov)
                {
                    if (item.InputIndex == 0) name = BlissBoxControllers.HatName(info.Type, info.Major);
                }
                else if (item.IsButton)
                {
                    int arrow = item.InputIndex - BlissBoxControllers.FirstArrowButton;
                    if (arrow >= 0 && arrow < arrowSeen.Length)
                    {
                        name = BlissBoxControllers.ArrowNames[arrow];
                        arrowSeen[arrow] = true;
                    }
                    else name = BlissBoxControllers.ButtonName(info.Type, info.Major, item.InputIndex);
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
                int index = BlissBoxControllers.FirstArrowButton + arrow;
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
                var names = BlissBoxControllers.PressureNames(info.Major);
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
