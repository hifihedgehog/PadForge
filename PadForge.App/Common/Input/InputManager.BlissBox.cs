using System;
using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Engine.Common.BlissBox;
using PadForge.Engine.Data;
using static SDL3.SDL;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        /// <summary>The switch the rows were last put in step with.</summary>
        private bool _blissBoxRowsReadRaw;

        /// <summary>Rows whose motors pass to their port on a later pass: the
        /// hand-off met another writer holding the row's output gate, which
        /// the poll thread never waits on. Poll thread only.</summary>
        private readonly HashSet<Guid> _blissBoxHandoffs = new();

        /// <summary>Rows whose motor caches go back to rest on a later pass
        /// after the switch went off, for the same reason.</summary>
        private readonly HashSet<Guid> _blissBoxCacheResets = new();

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
            bool wasOn = _blissBoxRowsReadRaw;
            // Every cycle while on, since a row can open between the switch
            // and this phase, and once more after it goes off.
            bool changed = (enabled || wasOn) && ReopenBlissBoxRows(enabled);
            _blissBoxRowsReadRaw = enabled;
            if (!enabled)
            {
                _blissBoxHandoffs.Clear();
                if (wasOn) QueueBlissBoxCacheResets();
                RetryBlissBoxRows(_blissBoxCacheResets, handOff: false);
                if (BlissBoxRuntime.Ports.Length == 0) return changed;
            }
            else _blissBoxCacheResets.Clear();

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
                foreach (var port in opened) _blissBoxHandoffs.Add(port.InstanceGuid);
            if (enabled) RetryBlissBoxRows(_blissBoxHandoffs, handOff: true);
            return changed;
        }

        /// <summary>Runs the hand-off or the cache reset for each pending row,
        /// keeping the rows whose output gate was taken for the next pass.</summary>
        private void RetryBlissBoxRows(HashSet<Guid> pending, bool handOff)
        {
            if (pending.Count == 0) return;
            List<Guid> done = null;
            foreach (var guid in pending)
            {
                var ud = FindOnlineDeviceByInstanceGuid(guid);
                if (ud == null || (handOff ? HandMotorsToBlissBox(ud) : ResetBlissBoxRumbleCache(ud)))
                    (done ??= new List<Guid>()).Add(guid);
            }
            if (done != null)
                foreach (var guid in done) pending.Remove(guid);
        }

        /// <summary>The switch went off: every port row's motor cache goes
        /// back to rest, so SDL's path takes the next level the game sends.</summary>
        private void QueueBlissBoxCacheResets()
        {
            var devices = SettingsManager.UserDevices;
            if (devices == null) return;
            lock (devices.SyncRoot)
                foreach (var ud in devices.Items)
                    if (ud?.ForceFeedbackState != null && ud.Device != null && BlissBoxProtocol.IsPort(ud.VendorId, ud.ProdId))
                        _blissBoxCacheResets.Add(ud.InstanceGuid);
        }

        /// <summary>
        /// A port is read raw while the switch is on and through SDL's
        /// gamepad mapping while it is off (<see cref="BlissBoxApi.ReadsRaw"/>).
        /// A row open the other way gets a fresh wrapper for the same SDL
        /// instance, the replug rebind's shape in Phase 1: the row loads the
        /// new wrapper and disposes the old one. SDL counts the opens of one
        /// joystick, so the device stays open across the swap.
        /// </summary>
        private bool ReopenBlissBoxRows(bool readRaw)
        {
            var devices = SettingsManager.UserDevices;
            if (devices == null) return false;
            List<UserDevice> stale = null;
            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                    if (ud != null && ud.IsOnline && ud.Device is SdlDeviceWrapper wrapper
                        && BlissBoxProtocol.IsPort(wrapper.VendorId, wrapper.ProductId)
                        && BlissBoxRowNeedsReopen(wrapper.GameController != IntPtr.Zero, readRaw))
                        (stale ??= new List<UserDevice>()).Add(ud);
            }
            if (stale == null) return false;

            bool changed = false;
            foreach (var ud in stale)
            {
                if (ud.Device is not SdlDeviceWrapper old) continue;
                uint id = old.SdlInstanceId;
                // With the switch off, a port SDL has no mapping for stays raw.
                if (!readRaw && !SDL_IsGamepad(id)) continue;
                var fresh = new SdlDeviceWrapper();
                bool loaded = false;
                try
                {
                    if (fresh.Open(id))
                    {
                        if (ud.InstanceGuid != fresh.InstanceGuid)
                            fresh.OverrideInstanceGuid(ud.InstanceGuid);
                        ud.LoadFromSdlDevice(fresh);
                        loaded = true;
                    }
                }
                catch (Exception ex)
                {
                    RaiseError($"Error reopening device (instance {id})", ex);
                }
                finally
                {
                    if (!loaded)
                        try { fresh.Dispose(); } catch { }
                }
                if (!loaded) continue;
                _openedSdlInstanceIds[fresh.SdlInstanceId] = fresh;
                Engine.SdlDiagLog.WriteLine(
                    $"DEV reopen SDL#{id} {fresh.VendorId:X4}:{fresh.ProductId:X4} gamepad={fresh.GameController != IntPtr.Zero} (Bliss-Box switch {(readRaw ? "on" : "off")})");
                changed = true;
            }
            return changed;
        }

        /// <summary>True when a port's row is open the other way from the
        /// switch: through the gamepad mapping while it is on, or raw while it
        /// is off.</summary>
        internal static bool BlissBoxRowNeedsReopen(bool openedAsGamepad, bool readRaw)
            => openedAsGamepad == readRaw;

        /// <summary>The moment a port's motors pass to the adapter's commands:
        /// an effect SDL started stops, and the row's motor cache starts from
        /// rest, so the next frame's levels reach the port. A row opened while
        /// the switch was off, on a port SDL found no motors on, gets its
        /// motor cache here, since Step 2 writes only rows that have one.
        /// False when another writer holds the row's output gate, so the
        /// cache waits for a later pass, as Step 2 skips a contested write.</summary>
        private static bool HandMotorsToBlissBox(UserDevice ud)
        {
            if (ud?.Device is not SdlDeviceWrapper wrapper) return true;
            // SDL's own stop needs no gate: nothing else drives SDL's rumble
            // for the port any more.
            try { wrapper.StopSdlRumble(); } catch { }
            if (!System.Threading.Monitor.TryEnter(ud.OutputSync)) return false;
            try
            {
                ud.ForceFeedbackState ??= new ForceFeedbackState();
                try { ud.ForceFeedbackState.StopDeviceForces(wrapper); } catch { }
            }
            finally { System.Threading.Monitor.Exit(ud.OutputSync); }
            return true;
        }

        /// <summary>Puts a row's motor cache back to rest after the switch
        /// went off. False when another writer holds its output gate.</summary>
        private static bool ResetBlissBoxRumbleCache(UserDevice ud)
        {
            if (ud.ForceFeedbackState == null || ud.Device == null) return true;
            if (!System.Threading.Monitor.TryEnter(ud.OutputSync)) return false;
            try { ud.ForceFeedbackState.StopDeviceForces(ud.Device); } catch { }
            finally { System.Threading.Monitor.Exit(ud.OutputSync); }
            return true;
        }
    }
}
