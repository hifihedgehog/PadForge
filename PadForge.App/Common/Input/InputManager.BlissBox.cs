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

        /// <summary>The switch's <see cref="BlissBoxRuntime.Generation"/> at
        /// the last pass.</summary>
        private int _blissBoxGeneration;

        /// <summary>Rows whose motors pass to their port on a later cycle: the
        /// hand-off met another writer holding the row's output gate, which
        /// the poll thread never waits on. Poll thread only.</summary>
        private readonly HashSet<Guid> _blissBoxHandoffs = new();

        /// <summary>Rows that give SDL their motor levels back on a later
        /// cycle after the switch went off: their retired port has not sent
        /// its final stop yet, or another writer holds the output gate.</summary>
        private readonly HashSet<Guid> _blissBoxCacheResets = new();

        /// <summary>
        /// Phase 1l (issue #469): one API sidecar per online Bliss-Box row
        /// while Read Bliss-Box Adapters is on. The rows are SDL's: a row open
        /// the other way from the switch gets a fresh wrapper, a port opens
        /// beside a row that appeared, and one closes when its row left,
        /// reconnected or the switch went off. A change of the switch brings
        /// this pass forward to the poll thread's next cycle
        /// (<see cref="BlissBoxSwitchChanged"/>), so the rows, the ports and
        /// the motors move together. Off and unchanged with no ports: three
        /// reads and out.
        /// </summary>
        private bool UpdateBlissBoxPorts()
        {
            // The generation first: a change that lands between the two reads
            // shows as a change on the next pass, never as none.
            int generation = BlissBoxRuntime.Generation;
            bool enabled = BlissBoxRuntime.Enabled;
            bool toggled = generation != _blissBoxGeneration;
            _blissBoxGeneration = generation;
            bool wasOn = _blissBoxRowsReadRaw;
            // Every cycle while on, since a row can open between the switch
            // and this phase, once more after it goes off, and after a switch
            // that went on and off again between two passes, since Phase 1
            // may have opened a row raw in between.
            bool changed = (enabled || wasOn || toggled) && ReopenBlissBoxRows(enabled);
            _blissBoxRowsReadRaw = enabled;
            if (!enabled && !wasOn && !toggled && BlissBoxRuntime.Ports.Length == 0)
            {
                RetryBlissBoxRows(_blissBoxCacheResets, handOff: false);
                return changed;
            }

            var rows = new List<BlissBoxRuntime.Row>();
            var devices = SettingsManager.UserDevices;
            if (enabled && devices != null)
            {
                lock (devices.SyncRoot)
                {
                    foreach (var ud in devices.Items)
                    {
                        if (ud == null || !ud.IsOnline || ud.Device is not SdlDeviceWrapper wrapper) continue;
                        if (!BlissBoxProtocol.IsPort(ud.VendorId, ud.ProdId) || string.IsNullOrEmpty(ud.DevicePath)) continue;
                        rows.Add(new BlissBoxRuntime.Row(ud.DevicePath, ud.ProdId, ud.InstanceGuid, wrapper.SdlInstanceId));
                    }
                }
            }

            // Switched off, this retires every port, and each worker sends its
            // final stop as it leaves.
            var opened = BlissBoxRuntime.Sync(rows);
            if (!enabled)
            {
                _blissBoxHandoffs.Clear();
                // SDL's rumble was stopped when the port took the motors, so
                // each row gives SDL its level back, once its port's final
                // stop has gone out (ResetBlissBoxRumbleCache).
                if (wasOn || toggled) QueueBlissBoxCacheResets();
                RetryBlissBoxRows(_blissBoxCacheResets, handOff: false);
                return changed;
            }
            _blissBoxCacheResets.Clear();
            if (opened != null)
                foreach (var port in opened) _blissBoxHandoffs.Add(port.InstanceGuid);
            // A switch that went off and on again since the last pass may
            // have let SDL's path drive a port it kept, so each one takes its
            // row's level again.
            if (toggled)
                foreach (var port in BlissBoxRuntime.Ports) _blissBoxHandoffs.Add(port.InstanceGuid);
            RetryBlissBoxRows(_blissBoxHandoffs, handOff: true);
            return changed;
        }

        /// <summary>True when the switch changed since Phase 1l last ran, so
        /// the poll loop runs Phase 1 on this cycle rather than on its
        /// enumeration interval. Poll thread only.</summary>
        private bool BlissBoxSwitchChanged => BlissBoxRuntime.Generation != _blissBoxGeneration;

        /// <summary>Every poll cycle: the hand-offs and SDL resends Phase 1l
        /// could not finish, so a contested output gate or a port still
        /// sending its final stop delays them by a cycle, not an enumeration
        /// interval. Nothing to do costs two counts.</summary>
        private void RetryPendingBlissBoxRows()
        {
            if (_blissBoxRowsReadRaw)
            {
                if (_blissBoxHandoffs.Count > 0) RetryBlissBoxRows(_blissBoxHandoffs, handOff: true);
            }
            else if (_blissBoxCacheResets.Count > 0) RetryBlissBoxRows(_blissBoxCacheResets, handOff: false);
        }

        /// <summary>Runs the hand-off or the cache reset for each pending row,
        /// keeping the rows it could not finish for a later cycle.</summary>
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

        /// <summary>The switch went off: every port row gives SDL the motor
        /// levels it last recorded (<see cref="ResetBlissBoxRumbleCache"/>).</summary>
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
        /// an effect SDL started stops, and the port takes the levels the row
        /// last recorded, whichever writer recorded them (Step 2, a relayed
        /// frame, or SDL's path before the switch), and tells both motors
        /// again (<see cref="BlissBoxRuntime.TakeMotors"/>). The row keeps its
        /// motor state through the switch's reopen, which is the same SDL
        /// connection (<see cref="UserDevice.SameConnection"/>), so a level a
        /// Remote Link peer sent once is still there. The port and the row's
        /// motor snapshot then agree, so the change detection that gates every
        /// later write holds for both. A row opened while the switch was off,
        /// on a port SDL found no motors on, gets its motor cache here, since
        /// Step 2 writes only rows that have one. False when another writer
        /// holds the row's output gate, so the hand-off waits for a later
        /// pass, as Step 2 skips a contested write.</summary>
        private static bool HandMotorsToBlissBox(UserDevice ud)
        {
            if (ud?.Device is not SdlDeviceWrapper wrapper) return true;
            // SDL's own stop needs no gate: nothing else drives SDL's rumble
            // for the port any more. The port's resend follows it in its
            // first step with report 17 read.
            try { wrapper.StopSdlRumble(); } catch { }
            if (!System.Threading.Monitor.TryEnter(ud.OutputSync)) return false;
            try
            {
                var state = ud.ForceFeedbackState ??= new ForceFeedbackState();
                BlissBoxRuntime.TakeMotors(ud.DevicePath, state.LeftMotorSpeed, state.RightMotorSpeed);
            }
            finally { System.Threading.Monitor.Exit(ud.OutputSync); }
            return true;
        }

        /// <summary>The motors back to SDL after the switch went off: SDL is
        /// told the levels the row last recorded and the cache records them as
        /// sent (<see cref="ForceFeedbackState.ResendScalar"/>). A Remote Link
        /// peer sends a steady level once, and SDL's rumble was stopped when
        /// the port took the motors, so waiting for the next change would
        /// leave them stopped. It waits until the row's retired port has sent
        /// its final stop, which on a GPA reaches the routines SDL's
        /// DirectInput effect drives (0x2E8E to 0x2EC3) and would end the
        /// level SDL was just given. False while that stop is pending or
        /// another writer holds the output gate.</summary>
        private static bool ResetBlissBoxRumbleCache(UserDevice ud)
        {
            if (ud.ForceFeedbackState == null || ud.Device == null) return true;
            if (BlissBoxRuntime.IsRetiring(ud.DevicePath)) return false;
            if (!System.Threading.Monitor.TryEnter(ud.OutputSync)) return false;
            try { ud.ForceFeedbackState.ResendScalar(ud.Device); } catch { }
            finally { System.Threading.Monitor.Exit(ud.OutputSync); }
            return true;
        }
    }
}
