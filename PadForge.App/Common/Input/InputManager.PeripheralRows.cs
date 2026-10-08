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

        /// <summary>The vendor rows this build opens, each with the software
        /// that stands behind it.</summary>
        internal static bool PeripheralRowWanted(PeripheralRowKind kind, PeripheralPresence presence) => kind switch
        {
            PeripheralRowKind.RazerSensa => presence.RazerSynapse && presence.SensaPlatform,
            _ => false,
        };

        /// <summary>Phase 1m (#494). A row opens while its vendor's software
        /// is installed and retires when it goes, and one the user removed
        /// from the Devices page comes back on the next pass, the G-keys
        /// row's recreate pattern. Nothing wanted and nothing open: one
        /// dictionary count and out.</summary>
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
            }
            return changed;
        }

        // Caller holds _peripheralRowsLock.
        private void RetirePeripheralRow(PeripheralRowKind kind)
        {
            if (!_peripheralRows.Remove(kind, out var row)) return;
            // A row going away mid-rumble owes its device a stop, the offline
            // path's rule for every other device. Its record may already be
            // gone, removed from the Devices page, so the stop keys on the id.
            PeripheralOutputs.StopHaptics(row.InstanceGuid);
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
            }
        }
    }
}
