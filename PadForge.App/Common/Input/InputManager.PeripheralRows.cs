using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // Vendor rows (#494): one per vendor whose software is installed,
        // opened and retired on the poll thread like the G-keys row, since
        // the row itself does no I/O. The presence it follows is read by the
        // peripheral host from the registry and files.
        private readonly Dictionary<PeripheralRowKind, PeripheralOutputRow> _peripheralRows = new();
        private readonly object _peripheralRowsLock = new object();
        private volatile bool _peripheralRowsSuppressed;

        // Unit rows (#494): one per lit unit no mouse or keyboard row
        // reaches, a headset or a light strip that Windows lists as a HID
        // LampArray. The peripheral host says which units those are
        // (PeripheralOutputs.UnitRows), and the rows open and retire here by
        // the vendor rows' rules. Guarded by _peripheralRowsLock.
        private readonly Dictionary<OutputPath, PeripheralUnitRow> _peripheralUnitRows = new();

        /// <summary>The vendor rows this build opens, each with the software
        /// that stands behind it.</summary>
        internal static bool PeripheralRowWanted(PeripheralRowKind kind, PeripheralPresence presence) => kind switch
        {
            PeripheralRowKind.RazerChroma => presence.RazerSynapse,
            PeripheralRowKind.LogitechLightsync => presence.LogitechLedSdk,
            PeripheralRowKind.RazerSensa => presence.RazerSynapse && presence.SensaPlatform,
            PeripheralRowKind.SteelSeriesGG => presence.SteelSeriesGG,
            _ => false,
        };

        /// <summary>Phase 1m (#494). A row opens while its vendor's software
        /// is installed and retires when it goes, and one the user removed
        /// from the Devices page comes back on the next pass, the G-keys
        /// row's recreate pattern. Every pass takes the lock and checks each
        /// kind, then the unit rows the same way.</summary>
        private bool UpdatePeripheralRows()
        {
            if (_peripheralRowsSuppressed) return false;
            var presence = PeripheralOutputs.Presence;
            bool changed = false;
            lock (_peripheralRowsLock)
            {
                if (_peripheralRowsSuppressed) return false;
                foreach (PeripheralRowKind kind in Enum.GetValues(typeof(PeripheralRowKind)))
                {
                    bool wanted = PeripheralRowWanted(kind, presence);
                    _peripheralRows.TryGetValue(kind, out var row);
                    if (row != null)
                    {
                        bool removedByUser = FindOnlineDeviceByInstanceGuid(row.InstanceGuid) == null;
                        if (!wanted || removedByUser)
                        {
                            RetirePeripheralRow(kind);
                            MarkChanged(ref changed, "peripheral", "- " + row.Name
                                + (!wanted ? " (software gone)" : " (removed by user)"));
                            row = null;
                        }
                    }
                    if (!wanted || row != null) continue;
                    try
                    {
                        var created = new PeripheralOutputRow(kind);
                        UserDevice ud = FindOrCreateUserDevice(created.InstanceGuid, created.ProductGuid);
                        ud.LoadFromExternalDevice(created);
                        ud.IsOnline = true;
                        _peripheralRows[kind] = created;
                        MarkChanged(ref changed, "peripheral", "+ " + created.Name);
                    }
                    catch (Exception ex)
                    {
                        RaiseError("Error opening the " + kind + " row", ex);
                    }
                }
                changed |= UpdatePeripheralUnitRows(PeripheralOutputs.UnitRows);
            }
            return changed;
        }

        /// <summary>The unit rows' half of the phase. A row opens for each
        /// unit the peripheral host lists and retires when its unit leaves
        /// the list: the device went, or a row of its own reaches it now. A
        /// row the user removed from the Devices page comes back on the same
        /// pass while its unit is still listed, as a vendor row does. Caller
        /// holds _peripheralRowsLock.</summary>
        private bool UpdatePeripheralUnitRows(IReadOnlyList<DirectUnit> wanted)
        {
            bool changed = false;
            List<OutputPath> retire = null;
            foreach (var pair in _peripheralUnitRows)
            {
                bool listed = false;
                foreach (var unit in wanted)
                    if (unit.Path == pair.Key) { listed = true; break; }
                bool removedByUser = listed && FindOnlineDeviceByInstanceGuid(pair.Value.InstanceGuid) == null;
                if (!listed || removedByUser) (retire ??= new List<OutputPath>()).Add(pair.Key);
                if (!listed) MarkChanged(ref changed, "peripheral", "- " + pair.Value.Name + " (unit gone)");
                else if (removedByUser) MarkChanged(ref changed, "peripheral", "- " + pair.Value.Name + " (removed by user)");
            }
            if (retire != null) foreach (var path in retire) RetirePeripheralUnitRow(path);

            foreach (var unit in wanted)
            {
                if (_peripheralUnitRows.ContainsKey(unit.Path)) continue;
                try
                {
                    var created = new PeripheralUnitRow(unit);
                    UserDevice ud = FindOrCreateUserDevice(created.InstanceGuid, created.ProductGuid);
                    ud.LoadFromExternalDevice(created);
                    ud.IsOnline = true;
                    _peripheralUnitRows[unit.Path] = created;
                    MarkChanged(ref changed, "peripheral", "+ " + created.Name);
                }
                catch (Exception ex)
                {
                    RaiseError("Error opening the row of " + PeripheralUnitRow.NameFor(unit), ex);
                }
            }
            return changed;
        }

        // Caller holds _peripheralRowsLock.
        private void RetirePeripheralRow(PeripheralRowKind kind)
        {
            if (!_peripheralRows.Remove(kind, out var row)) return;
            // A row going away mid-rumble owes its device a stop, the offline
            // path's rule for every other device, and its lighting goes back
            // to the vendor's software. Its record may already be gone,
            // removed from the Devices page, so both key on the id.
            PeripheralOutputs.StopHaptics(row.InstanceGuid);
            PeripheralOutputs.ReleaseDevice(row.InstanceGuid);
            var ud = FindOnlineDeviceByInstanceGuid(row.InstanceGuid);
            if (ud != null)
            {
                ud.IsOnline = false;
                ud.Device = null;
                NeutralizeMappedOutputsFor(ud);
            }
            row.Dispose();
        }

        // Caller holds _peripheralRowsLock.
        private void RetirePeripheralUnitRow(OutputPath unit)
        {
            if (!_peripheralUnitRows.Remove(unit, out var row)) return;
            // A unit row carries lighting alone, and its claims go with it,
            // so its backend hands the device back to its own effect. Its
            // record may already be gone, removed from the Devices page, so
            // the release keys on the id.
            PeripheralOutputs.ReleaseDevice(row.InstanceGuid);
            var ud = FindOnlineDeviceByInstanceGuid(row.InstanceGuid);
            if (ud != null)
            {
                ud.IsOnline = false;
                ud.Device = null;
                NeutralizeMappedOutputsFor(ud);
            }
            row.Dispose();
        }

        public void ShutdownPeripheralRows()
        {
            _peripheralRowsSuppressed = true;
            lock (_peripheralRowsLock)
            {
                foreach (var kind in new List<PeripheralRowKind>(_peripheralRows.Keys))
                    RetirePeripheralRow(kind);
                foreach (var unit in new List<OutputPath>(_peripheralUnitRows.Keys))
                    RetirePeripheralUnitRow(unit);
            }
        }

        // TouchSense mice bound to WinUSB (#494). Such a mouse is no Raw
        // Input mouse any more, so nothing above lists it: the lane that
        // drives it opens and retires a row for it, a mouse-type device it
        // reads over WinUSB, whose DevicePath is the mouse's WinUSB
        // interface path, so the peripheral host matches the row to the
        // mouse's TouchSense unit by container. Both methods are filled in
        // by that lane in a file of its own. Until then each call compiles
        // to nothing.

        /// <summary>Phase 1n. Opens and retires the row of each bound
        /// TouchSense mouse, and sets <paramref name="changed"/> when the
        /// device list changed, as every other Step 1 phase reports it.</summary>
        partial void UpdateTouchSenseMice(ref bool changed);

        /// <summary>Retires those rows when the manager is disposed, with
        /// the vendor rows and the unit rows.</summary>
        partial void ShutdownTouchSenseMice();

        /// <summary>Phase 1o. Opens and retires the row of each keyboard
        /// bound to PadForge's WinUSB driver (#549), and sets
        /// <paramref name="changed"/> when the device list changed.</summary>
        partial void UpdateWinUsbKeyboards(ref bool changed);

        /// <summary>Retires those rows when the manager is disposed.</summary>
        partial void ShutdownWinUsbKeyboards();
    }
}
