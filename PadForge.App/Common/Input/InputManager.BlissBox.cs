using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Engine.Common.BlissBox;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        /// <summary>
        /// Phase 1l (issue #469): one API sidecar per online Bliss-Box row
        /// while Read Bliss-Box Adapters is on. The rows are SDL's, so nothing
        /// is created or retired here beyond the ports themselves: a port
        /// opens beside a row that appeared and closes when its row left or
        /// the switch went off. Off with no ports: one volatile read and out.
        /// </summary>
        private bool UpdateBlissBoxPorts()
        {
            bool enabled = BlissBoxRuntime.Enabled;
            if (!enabled && BlissBoxRuntime.Ports.Length == 0)
                return false;

            var rows = new List<BlissBoxRuntime.Row>();
            var devices = SettingsManager.UserDevices;
            if (enabled && devices != null)
            {
                lock (devices.SyncRoot)
                {
                    foreach (var ud in devices.Items)
                    {
                        if (ud == null || !ud.IsOnline || ud.Device is not SdlDeviceWrapper) continue;
                        if (!BlissBoxProtocol.IsPort(ud.VendorId, ud.ProdId) || string.IsNullOrEmpty(ud.DevicePath)) continue;
                        rows.Add(new BlissBoxRuntime.Row(ud.DevicePath, ud.ProdId, ud.InstanceGuid));
                    }
                }
            }

            var opened = BlissBoxRuntime.Sync(rows);
            if (opened != null)
                foreach (var port in opened)
                    HandMotorsToBlissBox(FindOnlineDeviceByInstanceGuid(port.InstanceGuid));
            if (!enabled)
                ResetBlissBoxRumbleCaches(devices);
            return false;
        }

        /// <summary>The moment a port's motors pass to the adapter's commands:
        /// an effect SDL started stops, and the row's motor cache starts from
        /// rest, so the next frame's levels reach the port. A row opened while
        /// the switch was off, on a port SDL found no motors on, gets its
        /// motor cache here, since Step 2 writes only rows that have one.</summary>
        private static void HandMotorsToBlissBox(UserDevice ud)
        {
            if (ud?.Device is not SdlDeviceWrapper wrapper) return;
            lock (ud.OutputSync)
            {
                ud.ForceFeedbackState ??= new ForceFeedbackState();
                try { ud.ForceFeedbackState.StopDeviceForces(wrapper); } catch { }
                try { wrapper.StopSdlRumble(); } catch { }
            }
        }

        /// <summary>The switch went off: each port stopped its motors as it
        /// closed, so the rows' caches go back to rest and SDL's path takes
        /// the next level the game sends.</summary>
        private static void ResetBlissBoxRumbleCaches(DeviceCollection devices)
        {
            if (devices == null) return;
            var rows = new List<UserDevice>();
            lock (devices.SyncRoot)
                foreach (var ud in devices.Items)
                    if (ud?.ForceFeedbackState != null && ud.Device != null && BlissBoxProtocol.IsPort(ud.VendorId, ud.ProdId))
                        rows.Add(ud);
            foreach (var ud in rows)
                lock (ud.OutputSync)
                    try { ud.ForceFeedbackState.StopDeviceForces(ud.Device); } catch { }
        }
    }
}
