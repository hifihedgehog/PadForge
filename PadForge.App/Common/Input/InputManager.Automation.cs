using System;
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
                ud.LoadFromExternalDevice(device);
                ud.IsOnline = true;
            }

            device.CapabilitiesChanged -= OnProgramDeviceCapabilitiesChanged;
            device.CapabilitiesChanged += OnProgramDeviceCapabilitiesChanged;

            DevicesUpdated?.Invoke(this, EventArgs.Empty);
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
                            ud.LoadFromExternalDevice(program);
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
    /// The values programs hold controls of devices at (#597), as the poll
    /// thread reads them. A program's <c>own</c> line puts a value over a
    /// button, an axis, a slider or a hat of any device, and Step 2 applies
    /// it where the On-Screen Keyboard filter swaps what mappings read. The
    /// device's own state stays in <see cref="UserDevice.ReadInputState"/>,
    /// so a program that holds a button still hears it pressed.
    ///
    /// <para>Automation publishes a whole new table on every change, and the
    /// poll thread takes it with one read and no lock.</para>
    /// </summary>
    internal static class ControlHolds
    {
        /// <summary>The holds on one device. Written once before it is
        /// published, apart from its pool, which only the poll thread
        /// touches.</summary>
        internal sealed class DeviceHolds
        {
            public DeviceHolds((int Index, bool Down)[] buttons, (int Index, int Value)[] axes, (int Index, int Value)[] hats)
            {
                Buttons = buttons ?? Array.Empty<(int, bool)>();
                Axes = axes ?? Array.Empty<(int, int)>();
                Hats = hats ?? Array.Empty<(int, int)>();
            }

            /// <summary>Buttons by index, below 256.</summary>
            public readonly (int Index, bool Down)[] Buttons;

            /// <summary>Axes 0 to 23 and sliders 24 to 31, in
            /// <see cref="CustomInputState"/>'s range.</summary>
            public readonly (int Index, int Value)[] Axes;

            /// <summary>Hats 0 to 3, in hundredths of a degree or -1.</summary>
            public readonly (int Index, int Value)[] Hats;

            private PooledInputStatePair _pool;

            /// <summary>The state mappings read: the device's own with the
            /// held controls over it, in a buffer of this table's own.</summary>
            public CustomInputState Apply(CustomInputState read)
            {
                var s = _pool.Next();
                read.CopyInto(s);
                foreach (var (index, down) in Buttons)
                    if ((uint)index < (uint)s.Buttons.Length) s.Buttons[index] = down;
                foreach (var (index, value) in Axes)
                {
                    if (index < CustomInputState.MaxAxis) s.Axis[index] = value;
                    else if (index - CustomInputState.MaxAxis < CustomInputState.MaxSliders)
                        s.Sliders[index - CustomInputState.MaxAxis] = value;
                }
                foreach (var (index, value) in Hats)
                    if ((uint)index < (uint)s.Povs.Length) s.Povs[index] = value;
                return s;
            }
        }

        private static System.Collections.Generic.Dictionary<Guid, DeviceHolds> _current;

        /// <summary>The table in force, or null when nothing is held.</summary>
        internal static System.Collections.Generic.IReadOnlyDictionary<Guid, DeviceHolds> Current
            => System.Threading.Volatile.Read(ref _current);

        /// <summary>Puts a new table in force. Null or empty lets go of
        /// everything. The caller never changes a table it published.</summary>
        internal static void Publish(System.Collections.Generic.Dictionary<Guid, DeviceHolds> table)
            => System.Threading.Volatile.Write(ref _current, table == null || table.Count == 0 ? null : table);

        /// <summary>Step 2, for every device read: the state mappings read.
        /// The state given, untouched, when nothing of the device is
        /// held.</summary>
        internal static CustomInputState Apply(Guid device, CustomInputState state)
        {
            var table = System.Threading.Volatile.Read(ref _current);
            if (table == null || state == null || !table.TryGetValue(device, out var holds)) return state;
            return holds.Apply(state);
        }
    }
}
